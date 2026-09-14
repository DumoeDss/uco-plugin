/*
 * Design inspired by MCP for Unity (CoplayDev/unity-mcp), Copyright (c) Coplay Inc., MIT License.
 */

/*
┌──────────────────────────────────────────────────────────────────┐
│  Author: Ivan Murzak (https://github.com/IvanMurzak)             │
│  Repository: GitHub (https://github.com/IvanMurzak/Unity-MCP)    │
│  Copyright (c) 2025 Ivan Murzak                                  │
│  Licensed under the Apache License, Version 2.0.                 │
│  See the LICENSE file in the project root for more information.  │
└──────────────────────────────────────────────────────────────────┘
*/

#nullable enable
using System;
using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using com.IvanMurzak.McpPlugin;
using com.IvanMurzak.ReflectorNet.Utils;
using com.AtelierAI.Unity.Copilot.Editor.Utils;
using com.AtelierAI.Unity.Copilot.Runtime.Extensions;
using AIGD;
using UnityEditor;
using UnityEngine;
using Component = UnityEngine.Component;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    public partial class Tool_Vfx
    {
        public const string VfxGraphConfigureToolId = "vfx-graph-configure";

        [McpPluginTool
        (
            VfxGraphConfigureToolId,
            Title = "VFX / Graph / Configure",
            DestructiveHint = true
        )]
        [McpPluginSkillDescription("Set a single exposed property on a VFX Graph `VisualEffect` component. " +
            "Requires the `com.unity.visualeffectgraph` package — returns Ok=false with " +
            "'VFX Graph package (com.unity.visualeffectgraph) not installed.' when the package is missing. " +
            "Supports valueType ∈ { float, int, bool, vector2, vector3, vector4, color }.")]
        [McpPluginSkillBody("Set a single exposed property on a VisualEffect (VFX Graph) component. " +
            "The implementation is reflection-only — no hard dependency on the VFX Graph assembly — so this tool " +
            "is safe to compile even when the package is not installed. At runtime the tool probes for " +
            "`UnityEngine.VFX.VisualEffect` and returns a structured error when missing.\n\n" +
            "## Value parsing\n\n" +
            "`valueJson` is parsed by `System.Text.Json` against the shape implied by `valueType`:\n\n" +
            "- `float` / `int` — JSON number.\n" +
            "- `bool` — JSON boolean.\n" +
            "- `vector2` / `vector3` / `vector4` — JSON array of 2/3/4 numbers, e.g. `[0.5, 1.0, 0.0]`.\n" +
            "- `color` — JSON array of 4 numbers (RGBA in [0..1]); written via `SetVector4`.\n\n" +
            "## Behavior\n\n" +
            "Runs on the Unity main thread. Calls `VisualEffect.HasFloat/HasInt/HasBool/HasVector2/.../HasVector4` " +
            "(via reflection) to validate the property exists before calling the corresponding `SetX` method. " +
            "Records Undo and marks the component dirty.")]
        [Description("Set a single exposed property on a VFX Graph VisualEffect. " +
            "Returns Ok=false when the VFX Graph package is not installed.")]
        public VfxGraphResult ConfigureGraph
        (
            [Description("Target GameObject hosting the VisualEffect. Use 'gameobject-find' to locate it.")]
            GameObjectRef target,
            [Description("Property name in the VFX Graph asset (exposed parameter name).")]
            string propertyName,
            [Description("Value type: 'float' | 'int' | 'bool' | 'vector2' | 'vector3' | 'vector4' | 'color'.")]
            string valueType,
            [Description("JSON-encoded value matching valueType. Examples: '1.5' (float), 'true' (bool), '[0.5, 1.0]' (vector2).")]
            string valueJson
        )
        {
            if (target == null)
                return new VfxGraphResult { Ok = false, Error = Error.GameObjectRefRequired() };

            if (string.IsNullOrEmpty(propertyName))
                return new VfxGraphResult { Ok = false, Error = Error.MissingPropertyName() };

            if (string.IsNullOrEmpty(valueType))
                return new VfxGraphResult { Ok = false, Error = Error.MissingValueType() };

            if (valueJson == null)
                return new VfxGraphResult { Ok = false, Error = Error.MissingValueJson() };

            if (!target.IsValid(out var refErr))
                return new VfxGraphResult { Ok = false, Error = refErr };

            // Pre-flight: VFX Graph package must be installed for any of this to make sense.
            if (!IsVfxGraphAvailable())
                return new VfxGraphResult
                {
                    Ok = false,
                    PropertyName = propertyName,
                    ValueType = valueType,
                    Error = Error.VfxGraphNotInstalled()
                };

            return MainThread.Instance.Run(() =>
            {
                var go = target.FindGameObject(out var findErr);
                if (findErr != null || go == null)
                    return new VfxGraphResult
                    {
                        Ok = false,
                        Error = findErr ?? "GameObject not found.",
                        PropertyName = propertyName,
                        ValueType = valueType
                    };

                var vfxComponent = FindVisualEffect(go);
                if (vfxComponent == null)
                    return new VfxGraphResult
                    {
                        Ok = false,
                        GameObjectPath = GetHierarchyPath(go),
                        PropertyName = propertyName,
                        ValueType = valueType,
                        Error = Error.VisualEffectMissing(go.name)
                    };

                Undo.RecordObject(vfxComponent, $"Set VFX Graph {propertyName}");

                string? applyError = null;
                try
                {
                    applyError = ApplyVfxGraphValue(vfxComponent, propertyName, valueType, valueJson);
                }
                catch (Exception ex)
                {
                    applyError = $"Reflection invocation failed: {ex.Message}";
                }

                if (!string.IsNullOrEmpty(applyError))
                    return new VfxGraphResult
                    {
                        Ok = false,
                        GameObjectPath = GetHierarchyPath(go),
                        PropertyName = propertyName,
                        ValueType = valueType,
                        Error = applyError
                    };

                EditorUtility.SetDirty(vfxComponent);
                EditorUtility.SetDirty(go);
                EditorUtils.RepaintAllEditorWindows();

                return new VfxGraphResult
                {
                    Ok = true,
                    GameObjectPath = GetHierarchyPath(go),
                    PropertyName = propertyName,
                    ValueType = valueType
                };
            });
        }

        /// <summary>
        /// Applies a value to a VisualEffect component via reflection. Returns null on success, or an error
        /// message string. Assumes the VFX Graph package is available — the caller pre-flights that.
        /// </summary>
        private static string? ApplyVfxGraphValue(UnityEngine.Component vfx, string property, string valueType, string valueJson)
        {
            var t = vfx.GetType();
            var normalized = valueType.Trim().ToLowerInvariant();

            switch (normalized)
            {
                case "float":
                {
                    if (!InvokeHas(t, "HasFloat", vfx, property))
                        return Error.PropertyMissing(property);
                    if (!TryParseNumber(property, valueJson, out var f, out var parseErr))
                        return parseErr;
                    return InvokeSet(t, "SetFloat", vfx, property, f);
                }
                case "int":
                {
                    if (!InvokeHas(t, "HasInt", vfx, property))
                        return Error.PropertyMissing(property);
                    if (!TryParseInt(property, valueJson, out var i, out var parseErr))
                        return parseErr;
                    return InvokeSet(t, "SetInt", vfx, property, i);
                }
                case "bool":
                {
                    if (!InvokeHas(t, "HasBool", vfx, property))
                        return Error.PropertyMissing(property);
                    if (!TryParseBool(property, valueJson, out var b, out var parseErr))
                        return parseErr;
                    return InvokeSet(t, "SetBool", vfx, property, b);
                }
                case "vector2":
                {
                    if (!InvokeHas(t, "HasVector2", vfx, property))
                        return Error.PropertyMissing(property);
                    if (!TryParseNumberArray(property, valueJson, expectedLength: 2, out var arr, out var parseErr))
                        return parseErr;
                    return InvokeSet(t, "SetVector2", vfx, property, new Vector2(arr[0], arr[1]));
                }
                case "vector3":
                {
                    if (!InvokeHas(t, "HasVector3", vfx, property))
                        return Error.PropertyMissing(property);
                    if (!TryParseNumberArray(property, valueJson, expectedLength: 3, out var arr, out var parseErr))
                        return parseErr;
                    return InvokeSet(t, "SetVector3", vfx, property, new Vector3(arr[0], arr[1], arr[2]));
                }
                case "vector4":
                {
                    if (!InvokeHas(t, "HasVector4", vfx, property))
                        return Error.PropertyMissing(property);
                    if (!TryParseNumberArray(property, valueJson, expectedLength: 4, out var arr, out var parseErr))
                        return parseErr;
                    return InvokeSet(t, "SetVector4", vfx, property, new Vector4(arr[0], arr[1], arr[2], arr[3]));
                }
                case "color":
                {
                    // VFX Graph color exposes as a Vector4 internally; route through SetVector4
                    // after confirming a Vector4 handle exists.
                    if (!InvokeHas(t, "HasVector4", vfx, property))
                        return Error.PropertyMissing(property);
                    if (!TryParseNumberArray(property, valueJson, expectedLength: 4, out var arr, out var parseErr))
                        return parseErr;
                    return InvokeSet(t, "SetVector4", vfx, property, new Vector4(arr[0], arr[1], arr[2], arr[3]));
                }
                default:
                    return Error.UnknownValueType(valueType);
            }
        }

        // ---------------------------------------------------------------
        // Reflection helpers — bind by (name, ParameterTypes) to avoid ambiguity.
        // ---------------------------------------------------------------

        private static bool InvokeHas(Type t, string methodName, Component vfx, string property)
        {
            var m = t.GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance, binder: null,
                types: new[] { typeof(string) }, modifiers: null);
            if (m == null)
                return false; // Method not exposed — treat as "property missing" to surface a clear error.
            var result = m.Invoke(vfx, new object[] { property });
            return result is bool b && b;
        }

        private static string? InvokeSet(Type t, string methodName, Component vfx, string property, object value)
        {
            var paramType = value.GetType();
            var m = t.GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance, binder: null,
                types: new[] { typeof(string), paramType }, modifiers: null);
            if (m == null)
                return $"VisualEffect.{methodName}(string, {paramType.Name}) not found via reflection.";
            m.Invoke(vfx, new object[] { property, value });
            return null;
        }

        // ---------------------------------------------------------------
        // JSON value parsing (System.Text.Json).
        // ---------------------------------------------------------------

        private static bool TryParseNumber(string property, string json, out float value, out string? error)
        {
            value = 0f;
            error = null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Number)
                {
                    error = Error.InvalidValueJson(property, "expected a JSON number.");
                    return false;
                }
                value = (float)doc.RootElement.GetDouble();
                return true;
            }
            catch (Exception ex)
            {
                error = Error.InvalidValueJson(property, ex.Message);
                return false;
            }
        }

        private static bool TryParseInt(string property, string json, out int value, out string? error)
        {
            value = 0;
            error = null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Number)
                {
                    error = Error.InvalidValueJson(property, "expected a JSON integer.");
                    return false;
                }
                if (!doc.RootElement.TryGetInt32(out value))
                {
                    // Allow lossless conversion from double when it fits.
                    var d = doc.RootElement.GetDouble();
                    if (d < int.MinValue || d > int.MaxValue || Math.Floor(d) != d)
                    {
                        error = Error.InvalidValueJson(property, "value does not fit in Int32.");
                        return false;
                    }
                    value = (int)d;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = Error.InvalidValueJson(property, ex.Message);
                return false;
            }
        }

        private static bool TryParseBool(string property, string json, out bool value, out string? error)
        {
            value = false;
            error = null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                switch (doc.RootElement.ValueKind)
                {
                    case JsonValueKind.True: value = true; return true;
                    case JsonValueKind.False: value = false; return true;
                    default:
                        error = Error.InvalidValueJson(property, "expected a JSON boolean.");
                        return false;
                }
            }
            catch (Exception ex)
            {
                error = Error.InvalidValueJson(property, ex.Message);
                return false;
            }
        }

        private static bool TryParseNumberArray(string property, string json, int expectedLength,
            out float[] value, out string? error)
        {
            value = Array.Empty<float>();
            error = null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                {
                    error = Error.InvalidValueJson(property, $"expected a JSON array of length {expectedLength}.");
                    return false;
                }
                var len = doc.RootElement.GetArrayLength();
                if (len != expectedLength)
                {
                    error = Error.InvalidValueJson(property, $"expected length {expectedLength}, got {len}.");
                    return false;
                }
                var arr = new float[expectedLength];
                int i = 0;
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    if (el.ValueKind != JsonValueKind.Number)
                    {
                        error = Error.InvalidValueJson(property, $"element [{i}] is not a JSON number.");
                        return false;
                    }
                    arr[i++] = (float)el.GetDouble();
                }
                value = arr;
                return true;
            }
            catch (Exception ex)
            {
                error = Error.InvalidValueJson(property, ex.Message);
                return false;
            }
        }
    }
}
