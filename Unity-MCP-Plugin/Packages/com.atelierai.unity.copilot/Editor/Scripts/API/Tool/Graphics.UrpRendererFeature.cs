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
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using com.IvanMurzak.McpPlugin;
using com.IvanMurzak.ReflectorNet.Utils;
using JsonSerializer = System.Text.Json.JsonSerializer;
using UnityEditor;
using UnityEngine;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    public partial class Tool_Graphics
    {
        // ---------------------------------------------------------------
        // DTOs
        // ---------------------------------------------------------------

        public class RendererFeatureInfo
        {
            [Description("Concrete .NET type name of the renderer feature (e.g. 'RenderObjects').")]
            public string TypeName { get; set; } = string.Empty;

            [Description("Asset name of the renderer feature sub-asset (UnityEngine.Object.name).")]
            public string Name { get; set; } = string.Empty;

            [Description("ScriptableRendererFeature.isActive flag.")]
            public bool IsActive { get; set; }

            [Description("0-based index in the renderer feature list of the host ScriptableRendererData.")]
            public int Index { get; set; }
        }

        public class RendererFeatureListResult
        {
            [Description("Renderer features attached to the resolved ScriptableRendererData asset.")]
            public RendererFeatureInfo[] Features { get; set; } = Array.Empty<RendererFeatureInfo>();

            [Description("Resolved ScriptableRendererData asset path.")]
            public string? RendererDataPath { get; set; }

            [Description("Error message when the listing failed. Null on success.")]
            public string? Error { get; set; }
        }

        public class RendererFeatureResult
        {
            [Description("True when the operation completed without an error.")]
            public bool Ok { get; set; }

            [Description("Resolved ScriptableRendererData asset path.")]
            public string? RendererDataPath { get; set; }

            [Description("Error message when Ok is false. Null on success.")]
            public string? Error { get; set; }

            [Description("Affected renderer feature, when the operation has a single target. Null when irrelevant.")]
            public RendererFeatureInfo? Feature { get; set; }
        }

        // ---------------------------------------------------------------
        // graphics-urp-renderer-feature-list
        // ---------------------------------------------------------------

        public const string GraphicsUrpRendererFeatureListToolId = "graphics-urp-renderer-feature-list";

        [McpPluginTool
        (
            GraphicsUrpRendererFeatureListToolId,
            Title = "Graphics / URP Renderer Feature / List",
            ReadOnlyHint = true
        )]
        [McpPluginSkillDescription("Enumerate renderer features attached to a `ScriptableRendererData` asset " +
            "(URP). When `rendererDataPath` is omitted, the first renderer of the default URP asset is used.")]
        [McpPluginSkillBody("Loads a `ScriptableRendererData` (URP) and reads its `rendererFeatures` list via " +
            "reflection. Each entry exposes type name, asset name, active flag and 0-based index.")]
        [Description("Enumerate URP renderer features on a ScriptableRendererData asset.")]
        public RendererFeatureListResult ListRendererFeatures
        (
            [Description("ScriptableRendererData asset path. Optional — defaults to the URP asset's first renderer.")]
            string? rendererDataPath = null
        )
        {
            return MainThread.Instance.Run(() =>
            {
                if (!IsUrpAvailable())
                    return new RendererFeatureListResult { Error = UrpNotInstalledMessage };

                var data = ResolveRendererData(rendererDataPath, out var resolvedPath, out var err);
                if (data == null)
                    return new RendererFeatureListResult { Error = err, RendererDataPath = resolvedPath };

                var list = ReadFeatureList(data, out var listErr);
                if (list == null)
                    return new RendererFeatureListResult { Error = listErr, RendererDataPath = resolvedPath };

                var infos = new List<RendererFeatureInfo>(list.Count);
                for (int i = 0; i < list.Count; i++)
                {
                    var f = list[i] as ScriptableObject;
                    if (f == null) continue;
                    infos.Add(BuildFeatureInfo(f, i));
                }

                return new RendererFeatureListResult
                {
                    Features = infos.ToArray(),
                    RendererDataPath = resolvedPath
                };
            });
        }

        // ---------------------------------------------------------------
        // graphics-urp-renderer-feature-add
        // ---------------------------------------------------------------

        public const string GraphicsUrpRendererFeatureAddToolId = "graphics-urp-renderer-feature-add";

        [McpPluginTool
        (
            GraphicsUrpRendererFeatureAddToolId,
            Title = "Graphics / URP Renderer Feature / Add",
            DestructiveHint = true
        )]
        [McpPluginSkillDescription("Add a new `ScriptableRendererFeature` to a URP `ScriptableRendererData` asset. " +
            "The feature instance is created via `ScriptableObject.CreateInstance(featureType)` and attached " +
            "as a sub-asset via `AssetDatabase.AddObjectToAsset`.")]
        [McpPluginSkillBody("Resolves the renderer data asset, creates the feature, adds it both as a sub-asset " +
            "and as an entry in `rendererFeatures`, then marks the asset dirty and saves.\n\n" +
            "## Notes\n\n" +
            "- `featureTypeName` may be a fully-qualified type name (e.g. " +
            "`UnityEngine.Rendering.Universal.RenderObjects`) or the assembly-qualified spelling.\n" +
            "- Throws no exception — failures return Ok=false with a structured error.")]
        [Description("Add a ScriptableRendererFeature to a URP renderer data asset.")]
        public RendererFeatureResult AddRendererFeature
        (
            [Description("ScriptableRendererData asset path (required).")]
            string rendererDataPath,
            [Description("Type name of the feature, e.g. 'UnityEngine.Rendering.Universal.RenderObjects'.")]
            string featureTypeName
        )
        {
            if (string.IsNullOrEmpty(rendererDataPath))
                return new RendererFeatureResult { Ok = false, Error = "Parameter 'rendererDataPath' is required." };

            if (string.IsNullOrEmpty(featureTypeName))
                return new RendererFeatureResult { Ok = false, Error = "Parameter 'featureTypeName' is required." };

            return MainThread.Instance.Run(() =>
            {
                if (!IsUrpAvailable())
                    return new RendererFeatureResult
                    {
                        Ok = false,
                        RendererDataPath = rendererDataPath,
                        Error = UrpNotInstalledMessage
                    };

                var data = ResolveRendererData(rendererDataPath, out var resolvedPath, out var err);
                if (data == null)
                    return new RendererFeatureResult { Ok = false, RendererDataPath = resolvedPath, Error = err };

                var featureType = ResolveTypeAcrossAssemblies(featureTypeName);
                if (featureType == null)
                    return new RendererFeatureResult
                    {
                        Ok = false,
                        RendererDataPath = resolvedPath,
                        Error = $"Type '{featureTypeName}' not found. Provide a fully-qualified name."
                    };

                var baseType = GetRendererFeatureBaseType();
                if (baseType != null && !baseType.IsAssignableFrom(featureType))
                    return new RendererFeatureResult
                    {
                        Ok = false,
                        RendererDataPath = resolvedPath,
                        Error = $"Type '{featureType.FullName}' does not derive from ScriptableRendererFeature."
                    };

                try
                {
                    var feature = ScriptableObject.CreateInstance(featureType);
                    if (feature == null)
                        return new RendererFeatureResult
                        {
                            Ok = false,
                            RendererDataPath = resolvedPath,
                            Error = $"ScriptableObject.CreateInstance('{featureType.FullName}') returned null."
                        };

                    feature.name = featureType.Name;

                    var list = ReadFeatureList(data, out var listErr);
                    if (list == null)
                    {
                        UnityEngine.Object.DestroyImmediate(feature);
                        return new RendererFeatureResult
                        {
                            Ok = false,
                            RendererDataPath = resolvedPath,
                            Error = listErr
                        };
                    }

                    Undo.RegisterCreatedObjectUndo(feature, "Add URP Renderer Feature");
                    AssetDatabase.AddObjectToAsset(feature, data);
                    Undo.RecordObject(data, "Add URP Renderer Feature");
                    list.Add(feature);

                    EditorUtility.SetDirty(data);
                    AssetDatabase.SaveAssets();
                    AssetDatabase.ImportAsset(resolvedPath ?? AssetDatabase.GetAssetPath(data));

                    return new RendererFeatureResult
                    {
                        Ok = true,
                        RendererDataPath = resolvedPath,
                        Feature = BuildFeatureInfo(feature, list.Count - 1)
                    };
                }
                catch (Exception ex)
                {
                    return new RendererFeatureResult
                    {
                        Ok = false,
                        RendererDataPath = resolvedPath,
                        Error = ex.Message
                    };
                }
            });
        }

        // ---------------------------------------------------------------
        // graphics-urp-renderer-feature-remove
        // ---------------------------------------------------------------

        public const string GraphicsUrpRendererFeatureRemoveToolId = "graphics-urp-renderer-feature-remove";

        [McpPluginTool
        (
            GraphicsUrpRendererFeatureRemoveToolId,
            Title = "Graphics / URP Renderer Feature / Remove",
            DestructiveHint = true
        )]
        [McpPluginSkillDescription("Remove a renderer feature from a URP `ScriptableRendererData`. " +
            "Selectable by 0-based index or by type-name match.")]
        [McpPluginSkillBody("Resolves the renderer feature, removes it from the list, then destroys the " +
            "sub-asset via `AssetDatabase.RemoveObjectFromAsset`. Marks the renderer data dirty and saves.")]
        [Description("Remove a URP renderer feature by 0-based index or type-name match.")]
        public RendererFeatureResult RemoveRendererFeature
        (
            [Description("ScriptableRendererData asset path (required).")]
            string rendererDataPath,
            [Description("Type name (matches what was added) OR 0-based index.")]
            string typeNameOrIndex
        )
        {
            if (string.IsNullOrEmpty(rendererDataPath))
                return new RendererFeatureResult { Ok = false, Error = "Parameter 'rendererDataPath' is required." };

            if (string.IsNullOrEmpty(typeNameOrIndex))
                return new RendererFeatureResult { Ok = false, Error = "Parameter 'typeNameOrIndex' is required." };

            return MainThread.Instance.Run(() =>
            {
                if (!IsUrpAvailable())
                    return new RendererFeatureResult
                    {
                        Ok = false,
                        RendererDataPath = rendererDataPath,
                        Error = UrpNotInstalledMessage
                    };

                var data = ResolveRendererData(rendererDataPath, out var resolvedPath, out var err);
                if (data == null)
                    return new RendererFeatureResult { Ok = false, RendererDataPath = resolvedPath, Error = err };

                var list = ReadFeatureList(data, out var listErr);
                if (list == null)
                    return new RendererFeatureResult { Ok = false, RendererDataPath = resolvedPath, Error = listErr };

                int targetIndex = ResolveFeatureIndex(list, typeNameOrIndex);
                if (targetIndex < 0)
                    return new RendererFeatureResult
                    {
                        Ok = false,
                        RendererDataPath = resolvedPath,
                        Error = $"No renderer feature matched '{typeNameOrIndex}'."
                    };

                var feature = list[targetIndex] as ScriptableObject;
                if (feature == null)
                    return new RendererFeatureResult
                    {
                        Ok = false,
                        RendererDataPath = resolvedPath,
                        Error = $"Renderer feature at index {targetIndex} is null."
                    };

                try
                {
                    var info = BuildFeatureInfo(feature, targetIndex);

                    Undo.RecordObject(data, "Remove URP Renderer Feature");
                    list.RemoveAt(targetIndex);

                    AssetDatabase.RemoveObjectFromAsset(feature);
                    UnityEngine.Object.DestroyImmediate(feature, allowDestroyingAssets: true);

                    EditorUtility.SetDirty(data);
                    AssetDatabase.SaveAssets();
                    AssetDatabase.ImportAsset(resolvedPath ?? AssetDatabase.GetAssetPath(data));

                    return new RendererFeatureResult
                    {
                        Ok = true,
                        RendererDataPath = resolvedPath,
                        Feature = info
                    };
                }
                catch (Exception ex)
                {
                    return new RendererFeatureResult
                    {
                        Ok = false,
                        RendererDataPath = resolvedPath,
                        Error = ex.Message
                    };
                }
            });
        }

        // ---------------------------------------------------------------
        // graphics-urp-renderer-feature-configure
        // ---------------------------------------------------------------

        public const string GraphicsUrpRendererFeatureConfigureToolId = "graphics-urp-renderer-feature-configure";

        [McpPluginTool
        (
            GraphicsUrpRendererFeatureConfigureToolId,
            Title = "Graphics / URP Renderer Feature / Configure",
            DestructiveHint = true
        )]
        [McpPluginSkillDescription("Set a single field/property on a URP renderer feature by name. " +
            "Value is JSON-decoded then assigned via reflection.")]
        [McpPluginSkillBody("Resolves the renderer feature, JSON-decodes `valueJson`, then writes the value " +
            "to the named field/property via reflection. Supports common primitive types (bool/int/float/string) " +
            "plus arrays and `LayerMask`-like int representations.")]
        [Description("Set a single field on a URP renderer feature (JSON-encoded value).")]
        public RendererFeatureResult ConfigureRendererFeature
        (
            [Description("ScriptableRendererData asset path (required).")]
            string rendererDataPath,
            [Description("Type name (matches what was added) OR 0-based index.")]
            string typeNameOrIndex,
            [Description("Field name to set.")]
            string fieldName,
            [Description("JSON-encoded value.")]
            string valueJson
        )
        {
            if (string.IsNullOrEmpty(rendererDataPath))
                return new RendererFeatureResult { Ok = false, Error = "Parameter 'rendererDataPath' is required." };

            if (string.IsNullOrEmpty(typeNameOrIndex))
                return new RendererFeatureResult { Ok = false, Error = "Parameter 'typeNameOrIndex' is required." };

            if (string.IsNullOrEmpty(fieldName))
                return new RendererFeatureResult { Ok = false, Error = "Parameter 'fieldName' is required." };

            if (valueJson == null)
                return new RendererFeatureResult { Ok = false, Error = "Parameter 'valueJson' is required." };

            return MainThread.Instance.Run(() =>
            {
                if (!IsUrpAvailable())
                    return new RendererFeatureResult
                    {
                        Ok = false,
                        RendererDataPath = rendererDataPath,
                        Error = UrpNotInstalledMessage
                    };

                var data = ResolveRendererData(rendererDataPath, out var resolvedPath, out var err);
                if (data == null)
                    return new RendererFeatureResult { Ok = false, RendererDataPath = resolvedPath, Error = err };

                var list = ReadFeatureList(data, out var listErr);
                if (list == null)
                    return new RendererFeatureResult { Ok = false, RendererDataPath = resolvedPath, Error = listErr };

                int idx = ResolveFeatureIndex(list, typeNameOrIndex);
                if (idx < 0)
                    return new RendererFeatureResult
                    {
                        Ok = false,
                        RendererDataPath = resolvedPath,
                        Error = $"No renderer feature matched '{typeNameOrIndex}'."
                    };

                var feature = list[idx] as ScriptableObject;
                if (feature == null)
                    return new RendererFeatureResult
                    {
                        Ok = false,
                        RendererDataPath = resolvedPath,
                        Error = $"Renderer feature at index {idx} is null."
                    };

                try
                {
                    Undo.RecordObject(feature, $"Configure URP Renderer Feature.{fieldName}");

                    var memberType = GetMemberType(feature.GetType(), fieldName);
                    if (memberType == null)
                        return new RendererFeatureResult
                        {
                            Ok = false,
                            RendererDataPath = resolvedPath,
                            Error = $"Field '{fieldName}' not found on '{feature.GetType().FullName}'."
                        };

                    object? value;
                    try
                    {
                        value = DeserializeJsonForType(valueJson, memberType);
                    }
                    catch (Exception jsonEx)
                    {
                        return new RendererFeatureResult
                        {
                            Ok = false,
                            RendererDataPath = resolvedPath,
                            Error = $"Failed to parse valueJson for '{fieldName}': {jsonEx.Message}"
                        };
                    }

                    if (!TrySetMember(feature.GetType(), feature, fieldName, value!))
                        return new RendererFeatureResult
                        {
                            Ok = false,
                            RendererDataPath = resolvedPath,
                            Error = $"Failed to assign '{fieldName}' on '{feature.GetType().Name}'."
                        };

                    EditorUtility.SetDirty(feature);
                    EditorUtility.SetDirty(data);
                    AssetDatabase.SaveAssets();

                    return new RendererFeatureResult
                    {
                        Ok = true,
                        RendererDataPath = resolvedPath,
                        Feature = BuildFeatureInfo(feature, idx)
                    };
                }
                catch (Exception ex)
                {
                    return new RendererFeatureResult
                    {
                        Ok = false,
                        RendererDataPath = resolvedPath,
                        Error = ex.Message
                    };
                }
            });
        }

        // ---------------------------------------------------------------
        // ScriptableRendererData / feature reflection plumbing
        // ---------------------------------------------------------------

        /// <summary>
        /// Returns the URP `ScriptableRendererData` type or null when URP is not installed.
        /// </summary>
        internal static Type? GetRendererDataType()
        {
            try
            {
                return Type.GetType("UnityEngine.Rendering.Universal.ScriptableRendererData, Unity.RenderPipelines.Universal.Runtime", false);
            }
            catch { return null; }
        }

        /// <summary>
        /// Returns the URP `ScriptableRendererFeature` base type or null when URP is not installed.
        /// </summary>
        internal static Type? GetRendererFeatureBaseType()
        {
            try
            {
                return Type.GetType("UnityEngine.Rendering.Universal.ScriptableRendererFeature, Unity.RenderPipelines.Universal.Runtime", false);
            }
            catch { return null; }
        }

        /// <summary>
        /// Resolve a ScriptableRendererData asset by path. When `rendererDataPath` is empty, falls back
        /// to the first renderer of the default URP asset (via `rendererDataList`).
        /// </summary>
        private static ScriptableObject? ResolveRendererData(string? rendererDataPath, out string? resolvedPath, out string? error)
        {
            resolvedPath = rendererDataPath;
            error = null;

            var dataType = GetRendererDataType();
            if (dataType == null) { error = UrpNotInstalledMessage; return null; }

            if (!string.IsNullOrEmpty(rendererDataPath))
            {
                var so = AssetDatabase.LoadAssetAtPath<ScriptableObject>(rendererDataPath);
                if (so == null) { error = $"Asset not found at '{rendererDataPath}'."; return null; }
                if (!dataType.IsInstanceOfType(so))
                {
                    error = $"Asset at '{rendererDataPath}' is not a ScriptableRendererData.";
                    return null;
                }
                return so;
            }

            // Fall back: pull the first renderer of the default URP asset.
            var urpAssetType = GetUrpAssetType();
            var urp = QualitySettings.renderPipeline as ScriptableObject
                   ?? UnityEngine.Rendering.GraphicsSettings.defaultRenderPipeline as ScriptableObject;
            if (urp == null || urpAssetType == null || !urpAssetType.IsInstanceOfType(urp))
            {
                error = "rendererDataPath is empty and the default URP asset is not resolvable.";
                return null;
            }

            // 'm_RendererDataList' is the canonical private field on UniversalRenderPipelineAsset.
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var listField = urpAssetType.GetField("m_RendererDataList", flags)
                          ?? urpAssetType.GetField("rendererDataList", flags);
            if (listField?.GetValue(urp) is IEnumerable rendererList)
            {
                foreach (var item in rendererList)
                {
                    var so = item as ScriptableObject;
                    if (so == null) continue;
                    if (!dataType.IsInstanceOfType(so)) continue;
                    resolvedPath = AssetDatabase.GetAssetPath(so);
                    return so;
                }
            }

            error = "Could not enumerate ScriptableRendererData entries on the default URP asset.";
            return null;
        }

        /// <summary>
        /// Read `ScriptableRendererData.rendererFeatures` (the mutable List<ScriptableRendererFeature>)
        /// via reflection. Returns null + non-null `error` when the field is unavailable.
        /// </summary>
        private static IList? ReadFeatureList(ScriptableObject data, out string? error)
        {
            error = null;
            try
            {
                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                // 'm_RendererFeatures' is the private serialized field that backs 'rendererFeatures'.
                var field = data.GetType().GetField("m_RendererFeatures", flags)
                          ?? data.GetType().GetField("rendererFeatures", flags);
                if (field?.GetValue(data) is IList list) return list;

                var prop = data.GetType().GetProperty("rendererFeatures", flags);
                if (prop?.GetValue(data) is IList listFromProp) return listFromProp;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }

            error = "rendererFeatures list not found on ScriptableRendererData.";
            return null;
        }

        private static RendererFeatureInfo BuildFeatureInfo(ScriptableObject feature, int index)
        {
            return new RendererFeatureInfo
            {
                TypeName = feature.GetType().FullName ?? feature.GetType().Name,
                Name = feature.name,
                IsActive = ReadFeatureActive(feature),
                Index = index
            };
        }

        private static bool ReadFeatureActive(ScriptableObject feature)
        {
            try
            {
                var prop = feature.GetType().GetProperty("isActive",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (prop?.GetValue(feature) is bool b) return b;
            }
            catch { /* swallow */ }
            return true;
        }

        /// <summary>
        /// Resolve a renderer feature in the list by 0-based index or by exact-or-suffix type-name match.
        /// Returns -1 when nothing matches.
        /// </summary>
        private static int ResolveFeatureIndex(IList list, string typeNameOrIndex)
        {
            if (int.TryParse(typeNameOrIndex, out var idx))
            {
                if (idx < 0 || idx >= list.Count) return -1;
                return idx;
            }

            for (int i = 0; i < list.Count; i++)
            {
                var item = list[i] as ScriptableObject;
                if (item == null) continue;
                var type = item.GetType();
                if (string.Equals(type.FullName, typeNameOrIndex, StringComparison.Ordinal)) return i;
                if (string.Equals(type.Name, typeNameOrIndex, StringComparison.Ordinal)) return i;
                if (string.Equals(item.name, typeNameOrIndex, StringComparison.Ordinal)) return i;
            }
            return -1;
        }

        /// <summary>
        /// Best-effort cross-assembly type resolver. Tries `Type.GetType` first (assembly-qualified
        /// or naked when in mscorlib), then scans every loaded assembly by FullName / Name.
        /// </summary>
        private static Type? ResolveTypeAcrossAssemblies(string typeName)
        {
            var direct = Type.GetType(typeName, throwOnError: false);
            if (direct != null) return direct;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var t = asm.GetType(typeName, throwOnError: false);
                    if (t != null) return t;
                }
                catch { /* skip unloadable assemblies */ }
            }

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types; }
                catch { continue; }

                foreach (var t in types)
                {
                    if (t == null) continue;
                    if (string.Equals(t.FullName, typeName, StringComparison.Ordinal)) return t;
                    if (string.Equals(t.Name, typeName, StringComparison.Ordinal)) return t;
                }
            }
            return null;
        }

        /// <summary>
        /// Return the declared type of a public/non-public instance member (field or property), or null.
        /// </summary>
        private static Type? GetMemberType(Type owner, string memberName)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var field = owner.GetField(memberName, flags);
            if (field != null) return field.FieldType;
            var prop = owner.GetProperty(memberName, flags);
            return prop?.PropertyType;
        }

        /// <summary>
        /// JSON-decode a string into a CLR value compatible with <paramref name="target"/>. Falls back
        /// to common primitive shapes. Throws on shape mismatch so the caller can surface a clear error.
        /// </summary>
        private static object? DeserializeJsonForType(string json, Type target)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (target == typeof(bool))   return root.GetBoolean();
            if (target == typeof(int))    return root.GetInt32();
            if (target == typeof(long))   return root.GetInt64();
            if (target == typeof(float))  return (float)root.GetDouble();
            if (target == typeof(double)) return root.GetDouble();
            if (target == typeof(string))
                return root.ValueKind == JsonValueKind.Null ? null : root.GetString();

            if (target.IsEnum)
            {
                if (root.ValueKind == JsonValueKind.String)
                    return Enum.Parse(target, root.GetString()!, ignoreCase: true);
                if (root.ValueKind == JsonValueKind.Number)
                    return Enum.ToObject(target, Convert.ChangeType(root.GetInt64(), Enum.GetUnderlyingType(target)));
            }

            if (target == typeof(LayerMask))
                return (LayerMask)root.GetInt32();

            // Fallback: hand the raw JSON to System.Text.Json for complex types.
            return JsonSerializer.Deserialize(json, target);
        }
    }
}
