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
using com.AtelierAI.Uco.Framework;
using com.IvanMurzak.ReflectorNet.Utils;
using com.AtelierAI.Unity.Copilot.Editor.Utils;
using UnityEditor;
using UnityEngine;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    public partial class Tool_Graphics
    {
        public const string GraphicsVolumeSetFieldToolId = "graphics-volume-set-field";

        [UcoTool
        (
            GraphicsVolumeSetFieldToolId,
            Title = "Graphics / Volume / Set Field",
            DestructiveHint = true
        )]
        [UcoSkillDescription("Set the `.value` (and optionally `.overrideState`) of a single " +
            "`VolumeParameter<T>` field on a `VolumeComponent` override. Reflection-only — works without a " +
            "compile-time dependency on URP/HDRP. `valueJson` is parsed against the underlying `T` of the " +
            "parameter (float, int, bool, enum, Color, Vector2/3/4).")]
        [UcoSkillBody("Walks the VolumeComponent's reflection-discovered fields to find a " +
            "`VolumeParameter<T>` whose declared name matches `fieldName`. The parameter's `.value` is " +
            "written via reflection, and `.overrideState` is set to true by default (so the value actually " +
            "contributes to the blend).\n\n" +
            "## Value parsing\n\n" +
            "`valueJson` is parsed by `System.Text.Json` against the underlying T type:\n\n" +
            "- numeric T (`float`, `int`, `double`) — JSON number,\n" +
            "- `bool` — JSON boolean,\n" +
            "- enum — JSON string (enum name, case-insensitive) or JSON integer,\n" +
            "- `Color` — JSON array of 3 or 4 numbers (RGB[A] in [0..1]),\n" +
            "- `Vector2/3/4` — JSON array of matching length.\n\n" +
            "## Behavior\n\n" +
            "Runs on the Unity main thread. Returns Ok=false with a structured error when SRP Core is " +
            "missing, the override is not present on the profile, the field is not a `VolumeParameter<T>`, " +
            "or the JSON does not match T.")]
        [Description("Set a VolumeParameter<T> field on a VolumeComponent override (e.g. Bloom.intensity = 1.5). " +
            "Optionally toggles VolumeParameter.overrideState.")]
        public VolumeFieldResult SetField
        (
            [Description("Asset path of a VolumeProfile (*.asset) OR scene hierarchy path of a Volume GameObject.")]
            string profileOrVolumePath,
            [Description("Full or short type name of the VolumeComponent that owns the parameter.")]
            string componentTypeName,
            [Description("Parameter / field name on the VolumeComponent (e.g. 'intensity', 'threshold', 'tint').")]
            string fieldName,
            [Description("JSON-encoded value matching the parameter's underlying T type. " +
                "Examples: '1.5' (float), 'true' (bool), '[1,0,0,1]' (Color RGBA), '\"ACES\"' (enum).")]
            string valueJson,
            [Description("When true, also sets VolumeParameter.overrideState = true so the value contributes. " +
                "Default true.")]
            bool setOverrideState = true
        )
        {
            if (string.IsNullOrEmpty(fieldName))
                return new VolumeFieldResult { Ok = false, Error = "fieldName is required." };
            if (valueJson == null)
                return new VolumeFieldResult { Ok = false, Error = "valueJson is required." };

            return MainThread.Instance.Run(() =>
            {
                if (!IsSrpCoreAvailable())
                    return new VolumeFieldResult { Ok = false, Error = Error.SrpCoreNotInstalled() };

                var componentType = ResolveComponentType(componentTypeName);
                if (componentType == null)
                    return new VolumeFieldResult
                    {
                        Ok = false,
                        Error = Error.VolumeComponentTypeNotFound(componentTypeName)
                    };

                var vcBase = GetVolumeComponentType();
                if (vcBase == null || !vcBase.IsAssignableFrom(componentType))
                    return new VolumeFieldResult
                    {
                        Ok = false,
                        Error = Error.TypeIsNotVolumeComponent(componentTypeName)
                    };

                var profile = ResolveProfile(profileOrVolumePath, out var profilePath, out var resolveErr);
                if (profile == null)
                    return new VolumeFieldResult
                    {
                        Ok = false,
                        Error = resolveErr ?? Error.ProfilePathRequired()
                    };

                var component = CallProfileTryGet(profile, componentType);
                if (component == null)
                    return new VolumeFieldResult
                    {
                        Ok = false,
                        ProfileAssetPath = profilePath,
                        Error = Error.OverrideNotPresent(componentType.FullName ?? componentTypeName)
                    };

                // Locate the field. Search the actual runtime type (not the resolver type) to also pick up
                // base-class fields. VolumeParameter fields are declared as public instance fields.
                var field = FindParameterField(component.GetType(), fieldName);
                if (field == null)
                    return new VolumeFieldResult
                    {
                        Ok = false,
                        ProfileAssetPath = profilePath,
                        Error = Error.FieldNotFound(component.GetType().FullName ?? componentTypeName, fieldName)
                    };

                if (!IsVolumeParameter(field.FieldType))
                    return new VolumeFieldResult
                    {
                        Ok = false,
                        ProfileAssetPath = profilePath,
                        Error = Error.FieldNotVolumeParameter(component.GetType().FullName ?? componentTypeName, fieldName)
                    };

                var parameter = field.GetValue(component);
                if (parameter == null)
                    return new VolumeFieldResult
                    {
                        Ok = false,
                        ProfileAssetPath = profilePath,
                        Error = $"VolumeParameter '{componentTypeName}.{fieldName}' is null on the component instance."
                    };

                var underlyingT = GetVolumeParameterValueType(field.FieldType);
                if (underlyingT == null)
                    return new VolumeFieldResult
                    {
                        Ok = false,
                        ProfileAssetPath = profilePath,
                        Error = $"Could not determine VolumeParameter<T> underlying type for field '{fieldName}'."
                    };

                if (!TryParseJsonValue(underlyingT, valueJson, out var parsedValue, out var parseErr))
                    return new VolumeFieldResult
                    {
                        Ok = false,
                        ProfileAssetPath = profilePath,
                        Error = Error.InvalidValueJson(fieldName, parseErr ?? "parse error")
                    };

                Undo.RecordObject(component, $"Set {componentType.Name}.{fieldName}");

                try
                {
                    SetParameterValue(parameter, parsedValue);
                    if (setOverrideState)
                        SetParameterOverrideState(parameter, true);
                }
                catch (TargetInvocationException tie) when (tie.InnerException != null)
                {
                    return new VolumeFieldResult
                    {
                        Ok = false,
                        ProfileAssetPath = profilePath,
                        Error = $"Reflection write failed: {tie.InnerException.Message}"
                    };
                }
                catch (Exception ex)
                {
                    return new VolumeFieldResult
                    {
                        Ok = false,
                        ProfileAssetPath = profilePath,
                        Error = $"Reflection write failed: {ex.Message}"
                    };
                }

                var overrideStateAfter = GetParameterOverrideState(parameter);

                EditorUtility.SetDirty(component);
                EditorUtility.SetDirty(profile);
                if (!string.IsNullOrEmpty(profilePath))
                    AssetDatabase.SaveAssetIfDirty(profile);
                EditorUtils.RepaintAllEditorWindows();

                return new VolumeFieldResult
                {
                    Ok = true,
                    ProfileAssetPath = profilePath,
                    Field = new PostProcessFieldValue
                    {
                        TypeFullName = component.GetType().FullName ?? componentTypeName,
                        FieldName = fieldName,
                        ParameterTypeName = field.FieldType.Name,
                        ValueTypeName = underlyingT.Name,
                        OverrideState = overrideStateAfter
                    }
                };
            });
        }

        /// <summary>
        /// Look up an instance field by name across the type's full chain of base types. The default
        /// <c>GetField</c> with <see cref="BindingFlags.Public"/> already walks the chain, but a
        /// dedicated walk lets us also find private fields when callers reference them by name
        /// (some SRP versions made parameter fields non-public).
        /// </summary>
        private static FieldInfo? FindParameterField(Type t, string fieldName)
        {
            const BindingFlags flags =
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy;
            // FlattenHierarchy doesn't apply to instance fields — walk base types manually.
            var cursor = t;
            while (cursor != null && cursor != typeof(object))
            {
                var f = cursor.GetField(fieldName,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (f != null) return f;
                cursor = cursor.BaseType;
            }
            // Case-insensitive last resort.
            cursor = t;
            while (cursor != null && cursor != typeof(object))
            {
                foreach (var f in cursor.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (string.Equals(f.Name, fieldName, StringComparison.OrdinalIgnoreCase))
                        return f;
                }
                cursor = cursor.BaseType;
            }
            return null;
        }

        public class VolumeFieldResult
        {
            [Description("True on success.")]
            public bool Ok { get; set; }

            [Description("Resolved VolumeProfile asset path. Null for in-memory profiles.")]
            public string? ProfileAssetPath { get; set; }

            [Description("Per-field result describing what was written. Null on failure.")]
            public PostProcessFieldValue? Field { get; set; }

            [Description("Error message on failure. Null on success.")]
            public string? Error { get; set; }
        }
    }
}
