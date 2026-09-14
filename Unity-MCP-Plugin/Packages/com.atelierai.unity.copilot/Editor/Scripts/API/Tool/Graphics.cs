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
using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using com.IvanMurzak.McpPlugin;
using UnityEngine;
using Component = UnityEngine.Component;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    /// <summary>
    /// <para>
    /// Domain root for <c>manage_graphics</c>-family tools — graphics, rendering, lighting and
    /// post-processing operations.
    /// </para>
    /// <para>
    /// All SRP-specific types (<c>UnityEngine.Rendering.Volume</c>, <c>VolumeProfile</c>,
    /// <c>VolumeComponent</c>, <c>VolumeParameter&lt;T&gt;</c>, plus URP/HDRP component classes such
    /// as <c>Bloom</c>, <c>ColorAdjustments</c>, etc.) are accessed exclusively through reflection.
    /// The editor assembly declares <c>overrideReferences=true</c> in its <c>.asmdef</c>, so a hard
    /// compile-time reference to <c>com.unity.render-pipelines.core</c>, URP, or HDRP would prevent
    /// the plugin from compiling in projects that do not have those packages installed.
    /// </para>
    /// </summary>
    [McpPluginToolType]
    public partial class Tool_Graphics
    {
        // ---------------------------------------------------------------
        // Error / message helpers — kept centralised so error strings stay consistent across the
        // individual tool files of this domain.
        // ---------------------------------------------------------------
        public static class Error
        {
            public static string SrpCoreNotInstalled() =>
                "SRP Core package (com.unity.render-pipelines.core) is required for Volume operations " +
                "but was not found in the project.";

            public static string VolumeComponentTypeNotFound(string typeName) =>
                $"VolumeComponent type '{typeName}' was not found in any loaded assembly. " +
                "Verify the package providing it (URP / HDRP / custom) is installed and the type name " +
                "is the full name (e.g. 'UnityEngine.Rendering.Universal.Bloom') or class name.";

            public static string TypeIsNotVolumeComponent(string typeName) =>
                $"Type '{typeName}' is not a subclass of UnityEngine.Rendering.VolumeComponent.";

            public static string ProfilePathRequired() =>
                "profileOrVolumePath is required — pass either an asset path to a VolumeProfile " +
                "(*.asset) or a scene hierarchy path of a Volume GameObject.";

            public static string ProfileAssetNotFound(string path) =>
                $"VolumeProfile asset not found at path '{path}'.";

            public static string ProfileAssetWrongType(string path) =>
                $"Asset at path '{path}' is not a VolumeProfile.";

            public static string VolumeGameObjectNotFound(string path) =>
                $"GameObject at path '{path}' was not found in any opened scene.";

            public static string VolumeComponentMissingOnGo(string path) =>
                $"GameObject '{path}' does not host a UnityEngine.Rendering.Volume component.";

            public static string VolumeHasNoProfile(string path) =>
                $"Volume on '{path}' has no sharedProfile assigned.";

            public static string OverrideNotPresent(string typeName) =>
                $"VolumeProfile does not contain an override of type '{typeName}'.";

            public static string FieldNotFound(string typeName, string fieldName) =>
                $"Field '{fieldName}' was not found on VolumeComponent '{typeName}'.";

            public static string FieldNotVolumeParameter(string typeName, string fieldName) =>
                $"Field '{typeName}.{fieldName}' is not a VolumeParameter<T> subclass.";

            public static string InvalidValueJson(string fieldName, string detail) =>
                $"Failed to parse valueJson for field '{fieldName}': {detail}";

            public static string NameRequired() =>
                "name is required.";
        }

        // ---------------------------------------------------------------
        // Shared DTOs — surfaced by multiple tools in this domain.
        // ---------------------------------------------------------------

        /// <summary>
        /// Compact description of a single <c>VolumeComponent</c> sitting on a <c>VolumeProfile</c>.
        /// </summary>
        public class VolumeComponentInfo
        {
            [Description("Full type name of the VolumeComponent (e.g. 'UnityEngine.Rendering.Universal.Bloom').")]
            public string TypeFullName { get; set; } = "";

            [Description("Short class name (e.g. 'Bloom').")]
            public string TypeName { get; set; } = "";

            [Description("VolumeComponent.active flag — when false, none of its parameters contribute.")]
            public bool Active { get; set; }
        }

        /// <summary>
        /// Information about a single override write performed by <c>graphics-volume-add-override</c>
        /// or <c>graphics-volume-remove-override</c>.
        /// </summary>
        public class VolumeOverrideInfo
        {
            [Description("Full type name of the VolumeComponent that was added / removed.")]
            public string TypeFullName { get; set; } = "";

            [Description("VolumeComponent.active flag after the operation. Null when removed.")]
            public bool? Active { get; set; }
        }

        /// <summary>
        /// Per-field write result, as returned by <c>graphics-volume-set-field</c>.
        /// </summary>
        public class PostProcessFieldValue
        {
            [Description("VolumeComponent type that owns the field.")]
            public string TypeFullName { get; set; } = "";

            [Description("Field name as declared on the VolumeComponent (e.g. 'intensity').")]
            public string FieldName { get; set; } = "";

            [Description("VolumeParameter<T> concrete type (e.g. 'ClampedFloatParameter').")]
            public string ParameterTypeName { get; set; } = "";

            [Description("Underlying T type name of VolumeParameter<T> (e.g. 'Single').")]
            public string ValueTypeName { get; set; } = "";

            [Description("VolumeParameter.overrideState after the write.")]
            public bool OverrideState { get; set; }
        }

        // ---------------------------------------------------------------
        // SRP type resolvers — every Volume operation goes through these.
        // ---------------------------------------------------------------

        /// <summary>
        /// Resolves <c>UnityEngine.Rendering.Volume</c>. Lives in the SRP Core package
        /// (<c>Unity.RenderPipelines.Core.Runtime</c>) and is therefore optional.
        /// </summary>
        internal static Type? GetVolumeType()
        {
            try
            {
                return Type.GetType("UnityEngine.Rendering.Volume, Unity.RenderPipelines.Core.Runtime", false)
                    ?? FindLoadedType("UnityEngine.Rendering.Volume");
            }
            catch { return null; }
        }

        internal static Type? GetVolumeProfileType()
        {
            try
            {
                return Type.GetType("UnityEngine.Rendering.VolumeProfile, Unity.RenderPipelines.Core.Runtime", false)
                    ?? FindLoadedType("UnityEngine.Rendering.VolumeProfile");
            }
            catch { return null; }
        }

        internal static Type? GetVolumeComponentType()
        {
            try
            {
                return Type.GetType("UnityEngine.Rendering.VolumeComponent, Unity.RenderPipelines.Core.Runtime", false)
                    ?? FindLoadedType("UnityEngine.Rendering.VolumeComponent");
            }
            catch { return null; }
        }

        /// <summary>
        /// Open-generic <c>VolumeParameter&lt;T&gt;</c>. Concrete parameters (FloatParameter etc.)
        /// derive from a closed form of it.
        /// </summary>
        internal static Type? GetVolumeParameterOpenType()
        {
            try
            {
                return Type.GetType("UnityEngine.Rendering.VolumeParameter`1, Unity.RenderPipelines.Core.Runtime", false)
                    ?? FindLoadedType("UnityEngine.Rendering.VolumeParameter`1");
            }
            catch { return null; }
        }

        /// <summary>
        /// True when SRP Core is available — every Volume-family tool gates on this.
        /// </summary>
        internal static bool IsSrpCoreAvailable() =>
            GetVolumeProfileType() != null && GetVolumeComponentType() != null;

        // ---------------------------------------------------------------
        // VolumeComponent lookup — accepts a full-name or short-name token.
        // ---------------------------------------------------------------

        /// <summary>
        /// Looks up a <c>VolumeComponent</c>-derived type from any loaded assembly by full name first,
        /// then by short class name. The match is case-sensitive on full name, case-sensitive on
        /// short name with a final case-insensitive sweep as a last resort to be forgiving.
        /// </summary>
        internal static Type? ResolveComponentType(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return null;
            var vcBase = GetVolumeComponentType();
            if (vcBase == null) return null;

            // Fast path: full name through Type.GetType (any AQN-compatible token works too).
            var direct = Type.GetType(typeName, false);
            if (direct != null && vcBase.IsAssignableFrom(direct))
                return direct;

            Type? caseInsensitiveMatch = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types!; }
                catch { continue; }

                foreach (var t in types)
                {
                    if (t == null) continue;
                    if (!vcBase.IsAssignableFrom(t)) continue;
                    if (t.IsAbstract) continue;

                    if (t.FullName == typeName || t.Name == typeName)
                        return t;

                    if (caseInsensitiveMatch == null &&
                        (string.Equals(t.FullName, typeName, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(t.Name, typeName, StringComparison.OrdinalIgnoreCase)))
                    {
                        caseInsensitiveMatch = t;
                    }
                }
            }

            return caseInsensitiveMatch;
        }

        /// <summary>
        /// Scan all loaded assemblies for a type whose FullName matches exactly.
        /// </summary>
        private static Type? FindLoadedType(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type? t = null;
                try { t = asm.GetType(fullName, throwOnError: false); }
                catch { /* ignore */ }
                if (t != null) return t;
            }
            return null;
        }

        // ---------------------------------------------------------------
        // Volume / VolumeProfile reflection access — these methods rely on
        // public Unity APIs but call them through reflection to avoid a
        // compile-time dependency on the SRP packages.
        // ---------------------------------------------------------------

        internal static UnityEngine.Object? GetSharedProfile(UnityEngine.Component volume)
        {
            if (volume == null) return null;
            // Volume has a 'sharedProfile' property (VolumeProfile).
            var prop = volume.GetType().GetProperty("sharedProfile",
                BindingFlags.Public | BindingFlags.Instance);
            return prop?.GetValue(volume) as UnityEngine.Object;
        }

        internal static void SetSharedProfile(Component volume, UnityEngine.Object? profile)
        {
            if (volume == null) return;
            var prop = volume.GetType().GetProperty("sharedProfile",
                BindingFlags.Public | BindingFlags.Instance);
            prop?.SetValue(volume, profile);
        }

        internal static bool TryGetVolumeBool(Component volume, string propertyName, out bool value)
        {
            value = false;
            var prop = volume?.GetType().GetProperty(propertyName,
                BindingFlags.Public | BindingFlags.Instance);
            if (prop == null) return false;
            var v = prop.GetValue(volume);
            if (v is bool b) { value = b; return true; }
            return false;
        }

        internal static bool TryGetVolumeFloat(Component volume, string propertyName, out float value)
        {
            value = 0f;
            var prop = volume?.GetType().GetProperty(propertyName,
                BindingFlags.Public | BindingFlags.Instance);
            if (prop == null) return false;
            var v = prop.GetValue(volume);
            if (v is float f) { value = f; return true; }
            return false;
        }

        internal static void TrySetVolumeBool(Component volume, string propertyName, bool value)
        {
            var prop = volume?.GetType().GetProperty(propertyName,
                BindingFlags.Public | BindingFlags.Instance);
            prop?.SetValue(volume, value);
        }

        internal static void TrySetVolumeFloat(Component volume, string propertyName, float value)
        {
            var prop = volume?.GetType().GetProperty(propertyName,
                BindingFlags.Public | BindingFlags.Instance);
            prop?.SetValue(volume, value);
        }

        /// <summary>
        /// Returns the typed component list from a <c>VolumeProfile</c> — i.e. <c>profile.components</c>.
        /// The element type is <c>VolumeComponent</c>.
        /// </summary>
        internal static IList<UnityEngine.Object>? GetProfileComponents(UnityEngine.Object profile)
        {
            if (profile == null) return null;
            var prop = profile.GetType().GetProperty("components",
                BindingFlags.Public | BindingFlags.Instance);
            if (prop == null) return null;
            var raw = prop.GetValue(profile);
            if (raw is System.Collections.IEnumerable enumerable)
            {
                var list = new List<UnityEngine.Object>();
                foreach (var item in enumerable)
                {
                    if (item is UnityEngine.Object uo) list.Add(uo);
                }
                return list;
            }
            return null;
        }

        /// <summary>
        /// Calls <c>VolumeProfile.Add(Type, bool)</c> via reflection. Returns the created
        /// VolumeComponent on success, null on failure.
        /// </summary>
        internal static UnityEngine.Object? CallProfileAdd(UnityEngine.Object profile, Type componentType, bool overrides)
        {
            if (profile == null || componentType == null) return null;
            var add = profile.GetType().GetMethod("Add",
                BindingFlags.Public | BindingFlags.Instance, binder: null,
                types: new[] { typeof(Type), typeof(bool) }, modifiers: null);
            if (add == null) return null;
            return add.Invoke(profile, new object[] { componentType, overrides }) as UnityEngine.Object;
        }

        /// <summary>
        /// Calls <c>VolumeProfile.Remove(Type)</c> via reflection. Returns true on success.
        /// </summary>
        internal static bool CallProfileRemove(UnityEngine.Object profile, Type componentType)
        {
            if (profile == null || componentType == null) return false;
            // Some SRP versions: bool Remove(Type), some: bool Remove<T>(). Try non-generic first.
            var remove = profile.GetType().GetMethod("Remove",
                BindingFlags.Public | BindingFlags.Instance, binder: null,
                types: new[] { typeof(Type) }, modifiers: null);
            if (remove == null) return false;
            var result = remove.Invoke(profile, new object[] { componentType });
            return result is bool b ? b : true;
        }

        /// <summary>
        /// Calls <c>VolumeProfile.TryGet(Type, out VolumeComponent)</c> via reflection. Returns the
        /// component instance, or null when not present on the profile.
        /// </summary>
        internal static UnityEngine.Object? CallProfileTryGet(UnityEngine.Object profile, Type componentType)
        {
            if (profile == null || componentType == null) return null;

            // Iterate profile.components manually — TryGet via reflection with an out parameter is
            // possible but more brittle across SRP versions. The components list is the canonical
            // source of truth.
            var components = GetProfileComponents(profile);
            if (components == null) return null;

            foreach (var comp in components)
            {
                if (comp == null) continue;
                if (componentType.IsAssignableFrom(comp.GetType()))
                    return comp;
            }
            return null;
        }

        // ---------------------------------------------------------------
        // VolumeParameter<T> access — get/set the .value and .overrideState
        // public properties via reflection.
        // ---------------------------------------------------------------

        /// <summary>
        /// True when <paramref name="t"/> derives from open-generic <c>VolumeParameter&lt;T&gt;</c>.
        /// Falls back to walking inheritance because the open generic resolution can fail in some
        /// reflection paths.
        /// </summary>
        internal static bool IsVolumeParameter(Type t)
        {
            if (t == null) return false;
            var openGeneric = GetVolumeParameterOpenType();
            var cursor = t;
            while (cursor != null && cursor != typeof(object))
            {
                if (cursor.IsGenericType && openGeneric != null &&
                    cursor.GetGenericTypeDefinition() == openGeneric)
                {
                    return true;
                }
                if (cursor.FullName == "UnityEngine.Rendering.VolumeParameter`1") return true;
                cursor = cursor.BaseType;
            }
            return false;
        }

        /// <summary>
        /// Pulls the underlying <c>T</c> argument out of a <c>VolumeParameter&lt;T&gt;</c> subtype.
        /// </summary>
        internal static Type? GetVolumeParameterValueType(Type parameterType)
        {
            var cursor = parameterType;
            while (cursor != null && cursor != typeof(object))
            {
                if (cursor.IsGenericType &&
                    (cursor.GetGenericTypeDefinition().FullName == "UnityEngine.Rendering.VolumeParameter`1"))
                {
                    return cursor.GetGenericArguments()[0];
                }
                cursor = cursor.BaseType;
            }
            return null;
        }

        /// <summary>
        /// Set the <c>value</c> public property on a <c>VolumeParameter&lt;T&gt;</c> instance.
        /// </summary>
        internal static void SetParameterValue(object parameter, object? value)
        {
            var prop = parameter.GetType().GetProperty("value",
                BindingFlags.Public | BindingFlags.Instance);
            prop?.SetValue(parameter, value);
        }

        /// <summary>
        /// Set the <c>overrideState</c> public property on a <c>VolumeParameter</c> instance.
        /// </summary>
        internal static void SetParameterOverrideState(object parameter, bool overrideState)
        {
            var prop = parameter.GetType().GetProperty("overrideState",
                BindingFlags.Public | BindingFlags.Instance);
            prop?.SetValue(parameter, overrideState);
        }

        /// <summary>
        /// Read <c>overrideState</c> from a <c>VolumeParameter</c> — defaults to false on failure.
        /// </summary>
        internal static bool GetParameterOverrideState(object parameter)
        {
            var prop = parameter.GetType().GetProperty("overrideState",
                BindingFlags.Public | BindingFlags.Instance);
            var v = prop?.GetValue(parameter);
            return v is bool b && b;
        }

        // ---------------------------------------------------------------
        // JSON parsing for VolumeParameter<T>.value
        // ---------------------------------------------------------------

        /// <summary>
        /// Parses <paramref name="valueJson"/> into the runtime type expected by a
        /// <c>VolumeParameter&lt;T&gt;</c>'s underlying <c>T</c>. Supports primitives, enums,
        /// Color, Vector{2,3,4}.
        /// </summary>
        internal static bool TryParseJsonValue(Type targetType, string valueJson, out object? value, out string? error)
        {
            value = null;
            error = null;
            try
            {
                using var doc = JsonDocument.Parse(valueJson);
                var root = doc.RootElement;

                if (targetType == typeof(float))
                {
                    if (root.ValueKind != JsonValueKind.Number) { error = "expected a JSON number."; return false; }
                    value = (float)root.GetDouble();
                    return true;
                }
                if (targetType == typeof(double))
                {
                    if (root.ValueKind != JsonValueKind.Number) { error = "expected a JSON number."; return false; }
                    value = root.GetDouble();
                    return true;
                }
                if (targetType == typeof(int))
                {
                    if (root.ValueKind != JsonValueKind.Number) { error = "expected a JSON number."; return false; }
                    value = root.GetInt32();
                    return true;
                }
                if (targetType == typeof(bool))
                {
                    if (root.ValueKind == JsonValueKind.True) { value = true; return true; }
                    if (root.ValueKind == JsonValueKind.False) { value = false; return true; }
                    error = "expected a JSON boolean.";
                    return false;
                }
                if (targetType == typeof(string))
                {
                    if (root.ValueKind != JsonValueKind.String) { error = "expected a JSON string."; return false; }
                    value = root.GetString();
                    return true;
                }
                if (targetType.IsEnum)
                {
                    // Allow either string-name or integer encoding.
                    if (root.ValueKind == JsonValueKind.String)
                    {
                        value = Enum.Parse(targetType, root.GetString()!, ignoreCase: true);
                        return true;
                    }
                    if (root.ValueKind == JsonValueKind.Number)
                    {
                        value = Enum.ToObject(targetType, root.GetInt32());
                        return true;
                    }
                    error = "expected enum name (string) or integer.";
                    return false;
                }
                if (targetType == typeof(Color))
                {
                    if (!TryParseFloatArray(root, out var arr, out error)) return false;
                    if (arr.Length < 3 || arr.Length > 4) { error = "expected length 3 or 4 for Color."; return false; }
                    value = arr.Length == 4
                        ? new Color(arr[0], arr[1], arr[2], arr[3])
                        : new Color(arr[0], arr[1], arr[2], 1f);
                    return true;
                }
                if (targetType == typeof(Vector2))
                {
                    if (!TryParseFloatArray(root, out var arr, out error)) return false;
                    if (arr.Length != 2) { error = "expected length 2 for Vector2."; return false; }
                    value = new Vector2(arr[0], arr[1]);
                    return true;
                }
                if (targetType == typeof(Vector3))
                {
                    if (!TryParseFloatArray(root, out var arr, out error)) return false;
                    if (arr.Length != 3) { error = "expected length 3 for Vector3."; return false; }
                    value = new Vector3(arr[0], arr[1], arr[2]);
                    return true;
                }
                if (targetType == typeof(Vector4))
                {
                    if (!TryParseFloatArray(root, out var arr, out error)) return false;
                    if (arr.Length != 4) { error = "expected length 4 for Vector4."; return false; }
                    value = new Vector4(arr[0], arr[1], arr[2], arr[3]);
                    return true;
                }

                error = $"unsupported target type '{targetType.FullName}' — only primitives, enums, " +
                        "Color, Vector2/3/4 are supported.";
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static bool TryParseFloatArray(JsonElement root, out float[] arr, out string? error)
        {
            arr = Array.Empty<float>();
            error = null;
            if (root.ValueKind != JsonValueKind.Array) { error = "expected a JSON array."; return false; }
            var len = root.GetArrayLength();
            var result = new float[len];
            int i = 0;
            foreach (var el in root.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Number) { error = $"element [{i}] is not a JSON number."; return false; }
                result[i++] = (float)el.GetDouble();
            }
            arr = result;
            return true;
        }

        // ---------------------------------------------------------------
        // Profile / Volume resolution — accepts either an asset path or a scene GameObject path.
        // ---------------------------------------------------------------

        /// <summary>
        /// Resolves a <c>VolumeProfile</c> from the supplied <paramref name="profileOrVolumePath"/>:
        ///   - If it ends with <c>.asset</c>: loads via <c>AssetDatabase.LoadAssetAtPath</c>.
        ///   - Otherwise: treats it as a scene hierarchy path, finds the matching GameObject in any
        ///     opened scene, fetches the <c>Volume.sharedProfile</c>.
        /// Populates <paramref name="resolvedAssetPath"/> with the asset path when known.
        /// Must run on the main thread.
        /// </summary>
        internal static UnityEngine.Object? ResolveProfile(
            string profileOrVolumePath,
            out string? resolvedAssetPath,
            out string? error)
        {
            resolvedAssetPath = null;
            error = null;

            if (string.IsNullOrEmpty(profileOrVolumePath))
            {
                error = Error.ProfilePathRequired();
                return null;
            }

            if (profileOrVolumePath.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
            {
                var profileType = GetVolumeProfileType();
                if (profileType == null)
                {
                    error = Error.SrpCoreNotInstalled();
                    return null;
                }
                var asset = UnityEditor.AssetDatabase.LoadAssetAtPath(profileOrVolumePath, profileType);
                if (asset == null)
                {
                    error = Error.ProfileAssetNotFound(profileOrVolumePath);
                    return null;
                }
                if (!profileType.IsAssignableFrom(asset.GetType()))
                {
                    error = Error.ProfileAssetWrongType(profileOrVolumePath);
                    return null;
                }
                resolvedAssetPath = profileOrVolumePath;
                return asset;
            }

            // Treat as scene hierarchy path.
            var go = FindGameObjectByPath(profileOrVolumePath);
            if (go == null)
            {
                error = Error.VolumeGameObjectNotFound(profileOrVolumePath);
                return null;
            }

            var volumeType = GetVolumeType();
            if (volumeType == null)
            {
                error = Error.SrpCoreNotInstalled();
                return null;
            }
            var volume = go.GetComponent(volumeType);
            if (volume == null)
            {
                error = Error.VolumeComponentMissingOnGo(profileOrVolumePath);
                return null;
            }
            var profile = GetSharedProfile(volume);
            if (profile == null)
            {
                error = Error.VolumeHasNoProfile(profileOrVolumePath);
                return null;
            }
            resolvedAssetPath = UnityEditor.AssetDatabase.GetAssetPath(profile);
            if (string.IsNullOrEmpty(resolvedAssetPath))
                resolvedAssetPath = null;
            return profile;
        }

        /// <summary>
        /// Find a GameObject by slash-separated hierarchy path across all opened scenes. Returns
        /// null when no match is found. Must run on the main thread.
        /// </summary>
        internal static GameObject? FindGameObjectByPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
            {
                var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                {
                    var match = FindRecursive(root, path);
                    if (match != null) return match;
                }
            }
            return null;
        }

        /// <summary>
        /// Depth-first match of <paramref name="path"/> against <paramref name="root"/> and every
        /// descendant. Comparison is performed against the descendant's own hierarchy path so a
        /// caller can supply either the full root-included path or just the leaf name.
        /// </summary>
        private static GameObject? FindRecursive(GameObject root, string path)
        {
            if (string.Equals(GetHierarchyPath(root), path, StringComparison.Ordinal)) return root;
            foreach (Transform child in root.transform)
            {
                var match = FindRecursive(child.gameObject, path);
                if (match != null) return match;
            }
            return null;
        }

        /// <summary>
        /// Slash-separated hierarchy path of <paramref name="go"/> (e.g. <c>Root/Child/PostFX</c>).
        /// </summary>
        internal static string GetHierarchyPath(GameObject go)
        {
            if (go == null) return "";
            var t = go.transform;
            var path = t.name;
            while (t.parent != null)
            {
                t = t.parent;
                path = t.name + "/" + path;
            }
            return path;
        }

        // ---------------------------------------------------------------
        // Glob matching for sceneGlob — minimal '*' / '?' subset.
        // ---------------------------------------------------------------

        /// <summary>
        /// Lightweight glob matcher: <c>*</c> matches any (non-separator-aware) substring, <c>?</c>
        /// matches one char. When <paramref name="pattern"/> is null/empty the predicate accepts all.
        /// </summary>
        internal static bool MatchGlob(string? pattern, string input)
        {
            if (string.IsNullOrEmpty(pattern)) return true;
            return MatchGlobInternal(pattern!, 0, input ?? "", 0);
        }

        private static bool MatchGlobInternal(string p, int pi, string s, int si)
        {
            while (pi < p.Length)
            {
                var c = p[pi];
                if (c == '*')
                {
                    // Collapse consecutive '*'.
                    while (pi + 1 < p.Length && p[pi + 1] == '*') pi++;
                    if (pi == p.Length - 1) return true;
                    pi++;
                    for (int k = si; k <= s.Length; k++)
                    {
                        if (MatchGlobInternal(p, pi, s, k)) return true;
                    }
                    return false;
                }
                if (si >= s.Length) return false;
                if (c != '?' && c != s[si]) return false;
                pi++; si++;
            }
            return si == s.Length;
        }
    }
}
