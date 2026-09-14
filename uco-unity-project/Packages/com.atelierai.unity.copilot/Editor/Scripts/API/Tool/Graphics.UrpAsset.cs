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
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    public partial class Tool_Graphics
    {
        // ---------------------------------------------------------------
        // DTOs
        // ---------------------------------------------------------------

        /// <summary>
        /// Read/write snapshot of a URP `UniversalRenderPipelineAsset`. Property accessors are
        /// reflection-only so the plugin compiles even when the URP package is not installed.
        /// </summary>
        public class UrpAssetResult
        {
            [Description("True when the operation completed without an error.")]
            public bool Ok { get; set; }

            [Description("Asset path of the URP asset that was inspected or modified. Null when not resolved.")]
            public string? AssetPath { get; set; }

            [Description("UniversalRenderPipelineAsset.supportsHDR. Null when the field could not be read.")]
            public bool? Hdr { get; set; }

            [Description("UniversalRenderPipelineAsset.renderScale (0.1..2.0 in Unity). Null when not readable.")]
            public float? RenderScale { get; set; }

            [Description("UniversalRenderPipelineAsset.supportsCameraDepthTexture. Null when not readable.")]
            public bool? SupportsCameraDepthTexture { get; set; }

            [Description("UniversalRenderPipelineAsset.supportsCameraOpaqueTexture. Null when not readable.")]
            public bool? SupportsCameraOpaqueTexture { get; set; }

            [Description("MSAA sample-count enum name: 'Disabled' | '_2x' | '_4x' | '_8x'. " +
                "Null when not readable.")]
            public string? MsaaSampleCount { get; set; }

            [Description("Main-light rendering mode enum name (e.g. 'Disabled', 'PerPixel'). Null when not readable.")]
            public string? MainLightRenderingMode { get; set; }

            [Description("Additional-lights rendering mode enum name (e.g. 'Disabled', 'PerPixel', 'PerVertex'). " +
                "Null when not readable.")]
            public string? AdditionalLightsRenderingMode { get; set; }

            [Description("Maximum number of additional per-object lights. Null when not readable.")]
            public int? AdditionalLightsPerObjectLimit { get; set; }

            [Description("Error message when Ok is false. Null on success.")]
            public string? Error { get; set; }
        }

        // ---------------------------------------------------------------
        // graphics-urp-asset-get
        // ---------------------------------------------------------------

        public const string GraphicsUrpAssetGetToolId = "graphics-urp-asset-get";

        [UcoTool
        (
            GraphicsUrpAssetGetToolId,
            Title = "Graphics / URP Asset / Get",
            ReadOnlyHint = true
        )]
        [UcoSkillDescription("Read a `UniversalRenderPipelineAsset` and return its core knobs: HDR, " +
            "render scale, depth/opaque texture toggles, MSAA, main-light & additional-lights modes, additional-lights " +
            "per-object limit. Returns Ok=false with 'URP package not installed.' when URP is missing.")]
        [UcoSkillBody("Loads the URP asset at `assetPath` (defaulting to `QualitySettings.renderPipeline` " +
            "when omitted) and pulls each settable knob via reflection. Returns Ok=false when URP is not installed " +
            "or the asset is not a URP asset — never throws.")]
        [Description("Read URP UniversalRenderPipelineAsset settings.")]
        public UrpAssetResult GetUrpAsset
        (
            [Description("URP asset path. Default = QualitySettings.renderPipeline of the current Quality level.")]
            string? assetPath = null
        )
        {
            return MainThread.Instance.Run(() =>
            {
                if (!IsUrpAvailable())
                    return new UrpAssetResult { Ok = false, Error = UrpNotInstalledMessage };

                var asset = ResolveUrpAsset(assetPath, out var resolvedPath, out var err);
                if (asset == null)
                    return new UrpAssetResult { Ok = false, Error = err, AssetPath = resolvedPath };

                var result = new UrpAssetResult { Ok = true, AssetPath = resolvedPath };
                PopulateFromAsset(asset, result);
                return result;
            });
        }

        // ---------------------------------------------------------------
        // graphics-urp-asset-set
        // ---------------------------------------------------------------

        public const string GraphicsUrpAssetSetToolId = "graphics-urp-asset-set";

        [UcoTool
        (
            GraphicsUrpAssetSetToolId,
            Title = "Graphics / URP Asset / Set",
            DestructiveHint = true
        )]
        [UcoSkillDescription("Mutate `UniversalRenderPipelineAsset` settings. Pass only the fields you want " +
            "to change — null fields are left untouched. Writes are reflection-only so this is safe to call when URP " +
            "is absent (returns Ok=false instead of throwing).")]
        [UcoSkillBody("Loads the URP asset and writes each non-null parameter via reflection.\n\n" +
            "## Notes\n\n" +
            "- `msaaSampleCount` accepts `'Disabled' | '_2x' | '_4x' | '_8x'` (case-insensitive).\n" +
            "- `mainLightRenderingMode` / `additionalLightsRenderingMode` accept the enum-name strings of " +
            "`UniversalRenderPipelineAsset.LightRenderingMode` (typically `Disabled`, `PerPixel`, `PerVertex`).\n" +
            "- `additionalLightsPerObjectLimit` is clamped by URP itself to its supported range.\n\n" +
            "Marks the asset dirty and saves the AssetDatabase. Returns a fresh snapshot after the writes.")]
        [Description("Mutate URP UniversalRenderPipelineAsset settings (only non-null params are written).")]
        public UrpAssetResult SetUrpAsset
        (
            [Description("URP asset path (must point at a UniversalRenderPipelineAsset).")]
            string assetPath,
            [Description("UniversalRenderPipelineAsset.supportsHDR. Ignored when null.")]
            bool? hdr = null,
            [Description("UniversalRenderPipelineAsset.renderScale. Ignored when null.")]
            float? renderScale = null,
            [Description("UniversalRenderPipelineAsset.supportsCameraDepthTexture. Ignored when null.")]
            bool? supportsCameraDepthTexture = null,
            [Description("UniversalRenderPipelineAsset.supportsCameraOpaqueTexture. Ignored when null.")]
            bool? supportsCameraOpaqueTexture = null,
            [Description("MSAA sample count: 'Disabled' | '_2x' | '_4x' | '_8x'. Ignored when null.")]
            string? msaaSampleCount = null,
            [Description("Main-light rendering mode enum name (e.g. 'Disabled', 'PerPixel'). Ignored when null.")]
            string? mainLightRenderingMode = null,
            [Description("Additional-lights rendering mode enum name (e.g. 'Disabled', 'PerPixel', 'PerVertex'). Ignored when null.")]
            string? additionalLightsRenderingMode = null,
            [Description("Maximum number of additional per-object lights. Ignored when null.")]
            int? additionalLightsPerObjectLimit = null
        )
        {
            if (string.IsNullOrEmpty(assetPath))
                return new UrpAssetResult { Ok = false, Error = "Parameter 'assetPath' is required." };

            return MainThread.Instance.Run(() =>
            {
                if (!IsUrpAvailable())
                    return new UrpAssetResult { Ok = false, AssetPath = assetPath, Error = UrpNotInstalledMessage };

                var asset = ResolveUrpAsset(assetPath, out var resolvedPath, out var err);
                if (asset == null)
                    return new UrpAssetResult { Ok = false, Error = err, AssetPath = resolvedPath };

                try
                {
                    Undo.RecordObject(asset, "Set URP Asset Settings");

                    var t = asset.GetType();
                    if (hdr.HasValue)
                        TrySetMember(t, asset, "supportsHDR", hdr.Value);
                    if (renderScale.HasValue)
                        TrySetMember(t, asset, "renderScale", renderScale.Value);
                    if (supportsCameraDepthTexture.HasValue)
                        TrySetMember(t, asset, "supportsCameraDepthTexture", supportsCameraDepthTexture.Value);
                    if (supportsCameraOpaqueTexture.HasValue)
                        TrySetMember(t, asset, "supportsCameraOpaqueTexture", supportsCameraOpaqueTexture.Value);

                    if (!string.IsNullOrEmpty(msaaSampleCount))
                    {
                        if (!TryParseMsaaInt(msaaSampleCount!, out var msaaInt))
                            return new UrpAssetResult
                            {
                                Ok = false,
                                AssetPath = resolvedPath,
                                Error = $"Unknown msaaSampleCount '{msaaSampleCount}'. Expected 'Disabled' | '_2x' | '_4x' | '_8x'."
                            };
                        TrySetMember(t, asset, "msaaSampleCount", msaaInt);
                    }

                    if (!string.IsNullOrEmpty(mainLightRenderingMode))
                    {
                        var setErr = TrySetEnumByName(t, asset, "mainLightRenderingMode", mainLightRenderingMode!);
                        if (setErr != null)
                            return new UrpAssetResult { Ok = false, AssetPath = resolvedPath, Error = setErr };
                    }

                    if (!string.IsNullOrEmpty(additionalLightsRenderingMode))
                    {
                        var setErr = TrySetEnumByName(t, asset, "additionalLightsRenderingMode", additionalLightsRenderingMode!);
                        if (setErr != null)
                            return new UrpAssetResult { Ok = false, AssetPath = resolvedPath, Error = setErr };
                    }

                    if (additionalLightsPerObjectLimit.HasValue)
                    {
                        // URP exposes two related fields — try the per-object limit first, then fall
                        // back to the asset-wide cap.
                        if (!TrySetMember(t, asset, "additionalLightsPerObjectLimit", additionalLightsPerObjectLimit.Value))
                            TrySetMember(t, asset, "maxAdditionalLightsCount", additionalLightsPerObjectLimit.Value);
                    }

                    EditorUtility.SetDirty(asset);
                    AssetDatabase.SaveAssets();

                    var snapshot = new UrpAssetResult { Ok = true, AssetPath = resolvedPath };
                    PopulateFromAsset(asset, snapshot);
                    return snapshot;
                }
                catch (Exception ex)
                {
                    return new UrpAssetResult { Ok = false, AssetPath = resolvedPath, Error = ex.Message };
                }
            });
        }

        // ---------------------------------------------------------------
        // URP reflection helpers
        // ---------------------------------------------------------------

        internal const string UrpNotInstalledMessage = "URP package not installed.";

        /// <summary>
        /// Returns the URP `UniversalRenderPipelineAsset` <see cref="Type"/> or null when the URP
        /// package is not installed. Probed across the common assembly-qualified spellings.
        /// </summary>
        internal static Type? GetUrpAssetType()
        {
            try
            {
                return Type.GetType("UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset, Unity.RenderPipelines.Universal.Runtime", false)
                    ?? Type.GetType("UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset, Unity.RenderPipelines.Universal.Runtime, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null", false);
            }
            catch
            {
                return null;
            }
        }

        internal static bool IsUrpAvailable() => GetUrpAssetType() != null;

        /// <summary>
        /// Resolve a URP asset by path, defaulting to `QualitySettings.renderPipeline` when path is empty.
        /// Returns null + non-null <paramref name="error"/> when the asset cannot be loaded or is not a URP asset.
        /// </summary>
        private static ScriptableObject? ResolveUrpAsset(string? assetPath, out string? resolvedPath, out string? error)
        {
            resolvedPath = assetPath;
            error = null;

            var urpType = GetUrpAssetType();
            if (urpType == null)
            {
                error = UrpNotInstalledMessage;
                return null;
            }

            ScriptableObject? asset = null;
            if (string.IsNullOrEmpty(assetPath))
            {
                var pipeline = QualitySettings.renderPipeline as ScriptableObject
                              ?? GraphicsSettings.defaultRenderPipeline as ScriptableObject;
                if (pipeline == null)
                {
                    error = "No URP asset specified and QualitySettings.renderPipeline is null. Provide 'assetPath' explicitly.";
                    return null;
                }
                asset = pipeline;
                resolvedPath = AssetDatabase.GetAssetPath(pipeline);
            }
            else
            {
                asset = AssetDatabase.LoadAssetAtPath<ScriptableObject>(assetPath);
                if (asset == null)
                {
                    error = $"Asset not found at '{assetPath}'.";
                    return null;
                }
            }

            if (!urpType.IsInstanceOfType(asset))
            {
                error = $"Asset at '{resolvedPath}' is not a UniversalRenderPipelineAsset.";
                return null;
            }

            return asset;
        }

        /// <summary>
        /// Populate every settable field of <paramref name="result"/> from the URP asset by reading
        /// through reflection. Missing fields collapse to null on the result (already the default).
        /// </summary>
        private static void PopulateFromAsset(ScriptableObject asset, UrpAssetResult result)
        {
            var t = asset.GetType();

            result.Hdr                          = TryReadMember(t, asset, "supportsHDR") as bool?;
            result.RenderScale                  = TryReadFloat(t, asset, "renderScale");
            result.SupportsCameraDepthTexture   = TryReadMember(t, asset, "supportsCameraDepthTexture") as bool?;
            result.SupportsCameraOpaqueTexture  = TryReadMember(t, asset, "supportsCameraOpaqueTexture") as bool?;

            var msaa = TryReadMember(t, asset, "msaaSampleCount");
            if (msaa is int msaaInt)
                result.MsaaSampleCount = MsaaIntToName(msaaInt);

            result.MainLightRenderingMode       = TryReadMember(t, asset, "mainLightRenderingMode")?.ToString();
            result.AdditionalLightsRenderingMode = TryReadMember(t, asset, "additionalLightsRenderingMode")?.ToString();
            result.AdditionalLightsPerObjectLimit = TryReadInt(t, asset, "additionalLightsPerObjectLimit")
                                                  ?? TryReadInt(t, asset, "maxAdditionalLightsCount");
        }

        private static bool TryParseMsaaInt(string value, out int msaa)
        {
            switch (value.Trim().ToLowerInvariant())
            {
                case "disabled":
                case "0":
                case "1":
                    msaa = 1; return true;
                case "_2x":
                case "2x":
                case "2":
                    msaa = 2; return true;
                case "_4x":
                case "4x":
                case "4":
                    msaa = 4; return true;
                case "_8x":
                case "8x":
                case "8":
                    msaa = 8; return true;
                default:
                    msaa = 0; return false;
            }
        }

        private static string MsaaIntToName(int msaa) => msaa switch
        {
            <= 1 => "Disabled",
            2 => "_2x",
            4 => "_4x",
            8 => "_8x",
            _ => msaa.ToString()
        };

        // ---------------------------------------------------------------
        // generic reflection setters / readers — shared with the renderer
        // feature partial via Tool_Graphics partial scope.
        // ---------------------------------------------------------------

        internal static object? TryReadMember(Type t, object instance, string memberName)
        {
            try
            {
                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                var prop = t.GetProperty(memberName, flags);
                if (prop != null && prop.CanRead) return prop.GetValue(instance);
                var field = t.GetField(memberName, flags);
                if (field != null) return field.GetValue(instance);
            }
            catch { /* swallow */ }
            return null;
        }

        internal static float? TryReadFloat(Type t, object instance, string memberName)
        {
            var v = TryReadMember(t, instance, memberName);
            return v switch
            {
                float f => f,
                double d => (float)d,
                int i => i,
                _ => null
            };
        }

        internal static int? TryReadInt(Type t, object instance, string memberName)
        {
            var v = TryReadMember(t, instance, memberName);
            return v switch
            {
                int i => i,
                long l => (int)l,
                short s => s,
                uint ui => (int)ui,
                _ => null
            };
        }

        internal static bool TrySetMember(Type t, object instance, string memberName, object value)
        {
            try
            {
                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                var prop = t.GetProperty(memberName, flags);
                if (prop != null && prop.CanWrite)
                {
                    prop.SetValue(instance, CoerceValue(value, prop.PropertyType));
                    return true;
                }
                var field = t.GetField(memberName, flags);
                if (field != null)
                {
                    field.SetValue(instance, CoerceValue(value, field.FieldType));
                    return true;
                }
            }
            catch { /* swallow — caller can verify by re-reading */ }
            return false;
        }

        internal static string? TrySetEnumByName(Type t, object instance, string memberName, string enumName)
        {
            try
            {
                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                var prop = t.GetProperty(memberName, flags);
                Type? enumType = null;
                if (prop != null)
                {
                    enumType = prop.PropertyType;
                }
                else
                {
                    var field = t.GetField(memberName, flags);
                    if (field != null) enumType = field.FieldType;
                }

                if (enumType == null || !enumType.IsEnum)
                    return $"Member '{memberName}' is not an enum or does not exist on '{t.FullName}'.";

                if (!Enum.TryParse(enumType, enumName, ignoreCase: true, out var enumValue))
                {
                    var names = string.Join(", ", Enum.GetNames(enumType));
                    return $"Unknown value '{enumName}' for '{memberName}'. Expected one of: {names}.";
                }

                if (prop != null && prop.CanWrite) prop.SetValue(instance, enumValue);
                else t.GetField(memberName, flags)!.SetValue(instance, enumValue);
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        /// <summary>
        /// Coerce a primitive Json-decoded value to the destination type so reflection setters do not
        /// throw on `Int64`→`Int32`, `Double`→`Single`, etc.
        /// </summary>
        internal static object? CoerceValue(object? value, Type target)
        {
            if (value == null) return null;
            if (target.IsInstanceOfType(value)) return value;
            try
            {
                if (target.IsEnum)
                {
                    if (value is string s) return Enum.Parse(target, s, ignoreCase: true);
                    return Enum.ToObject(target, Convert.ChangeType(value, Enum.GetUnderlyingType(target)));
                }
                return Convert.ChangeType(value, target);
            }
            catch
            {
                return value;
            }
        }
    }
}
