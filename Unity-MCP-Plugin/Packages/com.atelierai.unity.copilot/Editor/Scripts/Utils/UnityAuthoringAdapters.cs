/*
┌──────────────────────────────────────────────────────────────────┐
│  Author: AtelierAI                                                 │
│  Copyright (c) 2025 AtelierAI                                     │
│  Licensed under the MIT License.                                  │
└──────────────────────────────────────────────────────────────────┘
*/
#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using AIGD;
using com.IvanMurzak.McpPlugin;
using com.IvanMurzak.McpPlugin.Common.Model;
using com.IvanMurzak.ReflectorNet.Utils;
using com.AtelierAI.Unity.Copilot.Editor.API;
using com.AtelierAI.Unity.Copilot.Runtime.Extensions;
using com.AtelierAI.Unity.Copilot.Runtime.Utils;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace com.AtelierAI.Unity.Copilot.Editor.Utils
{
    /// <summary>
    /// Read-only validator shared by the Scene/GameObject/Component pilot.
    /// It resolves the real Unity targets (objects, components, scenes,
    /// scene assets, and canonical paths) on the main thread without
    /// creating, modifying, destroying, dirtying, or saving anything. The
    /// reflected runner is deliberately never called from this adapter.
    /// </summary>
    public sealed class UnityPilotAuthoringValidator : IAuthoringValidator
    {
        public AuthoringValidationResult Validate(AuthoringInvocation invocation)
        {
            if (invocation == null)
                throw new ArgumentNullException(nameof(invocation));

            var inspection = UnityPilotAuthoringInspection.Inspect(invocation);
            return new AuthoringValidationResult
            {
                Valid = inspection.Errors.Count == 0,
                FailureCode = inspection.Errors.Count == 0 ? null : "validation_failed",
                Targets = inspection.Targets.Take(32).ToArray(),
                Details = inspection.Errors.Count == 0
                    ? null
                    : new System.Text.Json.Nodes.JsonObject
                    {
                        ["reason"] = inspection.Errors[0],
                        ["errorCount"] = inspection.Errors.Count,
                    },
            };
        }
    }

    /// <summary>
    /// Result of one read-only pilot inspection. Targets carry a state
    /// fingerprint derived from the live Unity object (identity, hierarchy
    /// path, owning scene/stage, component set) so a later change to the
    /// inspected target makes an issued confirmation plan stale.
    /// </summary>
    internal sealed class UnityPilotInspection
    {
        public List<AuthoringTargetSummary> Targets { get; } = new List<AuthoringTargetSummary>();
        public List<string> Errors { get; } = new List<string>();
        public List<string> Effects { get; } = new List<string>();
        public System.Text.Json.Nodes.JsonObject Before { get; } = new System.Text.Json.Nodes.JsonObject();
        public System.Text.Json.Nodes.JsonObject After { get; } = new System.Text.Json.Nodes.JsonObject();
    }

    /// <summary>
    /// Main-thread, read-only inspection of the pilot tools' targets. Every
    /// lookup uses the same resolution helpers the runners use
    /// (<c>GameObjectUtils.FindBy</c>, <c>FindAssetObject</c>,
    /// <c>ComponentRef.Matches</c>), but no Unity API with a side effect is
    /// reachable from this type.
    /// </summary>
    internal static class UnityPilotAuthoringInspection
    {
        private const int MaxListedTargets = 32;

        public static UnityPilotInspection Inspect(AuthoringInvocation invocation)
            => MainThread.Instance.Run(() => InspectOnMainThread(invocation));

        private static UnityPilotInspection InspectOnMainThread(AuthoringInvocation invocation)
        {
            var result = new UnityPilotInspection();
            foreach (var binding in invocation.PathBindings.Values)
            {
                if (binding.Resolution.IsReadOnly
                    && binding.Resolution.Intent != ProjectPathAccessIntent.Read)
                    result.Errors.Add("path_read_only");
            }

            switch (invocation.Name)
            {
                case Tool_GameObject.GameObjectCreateToolId:
                    InspectGameObjectCreate(invocation, result);
                    break;
                case Tool_GameObject.GameObjectDestroyToolId:
                    InspectSingleGameObject(invocation, result, "gameObjectRef", "destroy");
                    break;
                case Tool_GameObject.GameObjectModifyToolId:
                    InspectGameObjectList(invocation, result, "modify");
                    break;
                case Tool_GameObject.GameObjectDuplicateToolId:
                    InspectGameObjectList(invocation, result, "duplicate");
                    break;
                case Tool_GameObject.GameObjectSetParentToolId:
                    InspectGameObjectList(invocation, result, "parent");
                    InspectOptionalOrRequiredGameObject(invocation, result, "parentGameObjectRef", required: true, kind: "Parent");
                    break;
                case Tool_GameObject.GameObjectComponentAddToolId:
                    InspectComponentAdd(invocation, result);
                    break;
                case Tool_GameObject.GameObjectComponentModifyToolId:
                    InspectComponentModify(invocation, result);
                    break;
                case Tool_GameObject.GameObjectComponentDestroyToolId:
                    InspectComponentDestroy(invocation, result);
                    break;
                case Tool_Scene.SceneOpenToolId:
                    InspectSceneAsset(invocation, result, requireOpened: false, effect: "open scene");
                    break;
                case Tool_Scene.SceneSetActiveToolId:
                    InspectSceneAsset(invocation, result, requireOpened: true, effect: "set active scene");
                    break;
                case Tool_Scene.SceneCreateToolId:
                    InspectSceneCreate(invocation, result);
                    break;
                case Tool_Scene.SceneSaveToolId:
                    InspectSceneSave(invocation, result);
                    break;
                case Tool_Scene.SceneUnloadToolId:
                    InspectSceneUnload(invocation, result);
                    break;
                default:
                    InspectArgumentTargets(invocation, result);
                    break;
            }

            result.Before["targetCount"] = result.Targets.Count;
            result.Before["activeScene"] = SafeSceneDisplay(EditorSceneManager.GetActiveScene());
            result.After["targetCount"] = result.Targets.Count;
            result.After["predictedEffects"] = result.Effects.Take(32).Aggregate(
                new System.Text.Json.Nodes.JsonArray(),
                (array, effect) =>
                {
                    array.Add(effect);
                    return array;
                });
            return result;
        }

        private static void InspectGameObjectCreate(AuthoringInvocation invocation, UnityPilotInspection result)
        {
            TryGetString(invocation.Arguments, "name", out var name);
            if (string.IsNullOrEmpty(name))
                result.Errors.Add("name_missing");

            var parentFingerprint = string.Empty;
            if (TryGetArgument(invocation.Arguments, "parentGameObjectRef", out var parentElement)
                && parentElement.ValueKind == JsonValueKind.Object)
            {
                var parentRef = Deserialize<GameObjectRef>(parentElement);
                if (parentRef != null && parentRef.IsValid(out _))
                {
                    var parent = GameObjectUtils.FindBy(parentRef, out var error);
                    if (parent == null)
                    {
                        result.Errors.Add("parent_not_found");
                    }
                    else
                    {
                        var target = GameObjectTarget(parent, "Parent");
                        parentFingerprint = target.Fingerprint ?? string.Empty;
                        result.Targets.Add(target);
                    }
                }
            }

            var container = ContainerFingerprint();
            result.Targets.Add(new AuthoringTargetSummary
            {
                Kind = "GameObject",
                Name = Bound(name),
                Fingerprint = Hash("create|" + name + "|" + container + "|" + parentFingerprint),
            });
            result.Effects.Add("create GameObject '" + Bound(name) + "'");
        }

        private static void InspectSingleGameObject(
            AuthoringInvocation invocation,
            UnityPilotInspection result,
            string argumentName,
            string effect)
        {
            var go = ResolveRequiredGameObject(invocation, result, argumentName, "GameObject");
            if (go == null)
                return;
            result.Effects.Add(effect + " GameObject '" + Bound(go.name) + "' (" + go.transform.childCount + " children)");
        }

        private static void InspectOptionalOrRequiredGameObject(
            AuthoringInvocation invocation,
            UnityPilotInspection result,
            string argumentName,
            bool required,
            string kind)
        {
            if (!TryGetArgument(invocation.Arguments, argumentName, out var element)
                || element.ValueKind != JsonValueKind.Object)
            {
                if (required)
                    result.Errors.Add("target_missing");
                return;
            }

            var reference = Deserialize<GameObjectRef>(element);
            if (reference == null || !reference.IsValid(out _))
            {
                if (required)
                    result.Errors.Add("target_invalid");
                return;
            }

            var go = GameObjectUtils.FindBy(reference, out _);
            if (go == null)
            {
                result.Errors.Add("target_not_found");
                return;
            }
            result.Targets.Add(GameObjectTarget(go, kind));
        }

        private static void InspectGameObjectList(
            AuthoringInvocation invocation,
            UnityPilotInspection result,
            string effect)
        {
            if (!TryGetArgument(invocation.Arguments, "gameObjectRefs", out var element)
                || element.ValueKind != JsonValueKind.Array)
            {
                result.Errors.Add("target_missing");
                return;
            }

            var list = Deserialize<GameObjectRefList>(element);
            if (list == null || list.Count == 0)
            {
                result.Errors.Add("target_missing");
                return;
            }

            var resolved = 0;
            foreach (var reference in list)
            {
                if (reference == null || !reference.IsValid(out _))
                {
                    result.Errors.Add("target_invalid");
                    continue;
                }
                var go = GameObjectUtils.FindBy(reference, out _);
                if (go == null)
                {
                    result.Errors.Add("target_not_found");
                    continue;
                }
                resolved++;
                if (result.Targets.Count < MaxListedTargets)
                    result.Targets.Add(GameObjectTarget(go, "GameObject"));
            }
            result.Effects.Add(effect + " " + resolved + " GameObject(s)");
        }

        private static void InspectComponentAdd(AuthoringInvocation invocation, UnityPilotInspection result)
        {
            var go = ResolveRequiredGameObject(invocation, result, "gameObjectRef", "GameObject");
            if (!TryGetArgument(invocation.Arguments, "componentNames", out var namesElement)
                || namesElement.ValueKind != JsonValueKind.Array)
            {
                result.Errors.Add("component_names_missing");
                return;
            }

            var count = 0;
            foreach (var item in namesElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
                {
                    result.Errors.Add("component_name_invalid");
                    continue;
                }

                var componentName = item.GetString()!;
                var type = TypeUtils.GetType(componentName)
                    ?? Tool_GameObject.AllComponentTypes.FirstOrDefault(t => t.Name == componentName);
                if (type == null)
                {
                    result.Errors.Add("component_type_not_found");
                    continue;
                }
                if (!typeof(UnityEngine.Component).IsAssignableFrom(type))
                {
                    result.Errors.Add("component_type_not_component");
                    continue;
                }
                count++;
                result.Targets.Add(new AuthoringTargetSummary
                {
                    Kind = "ComponentType",
                    Name = Bound(type.FullName),
                    Fingerprint = Hash("component-type|" + type.AssemblyQualifiedName),
                });
            }

            if (count == 0 && result.Errors.Count == 0)
                result.Errors.Add("component_names_missing");
            if (go != null)
                result.Effects.Add("add " + count + " component(s) to GameObject '" + Bound(go.name) + "'");
        }

        private static void InspectComponentModify(AuthoringInvocation invocation, UnityPilotInspection result)
        {
            var go = ResolveRequiredGameObject(invocation, result, "gameObjectRef", "GameObject");
            if (go == null)
                return;

            if (!TryGetArgument(invocation.Arguments, "componentRef", out var element)
                || element.ValueKind != JsonValueKind.Object)
            {
                result.Errors.Add("component_missing");
                return;
            }

            var reference = Deserialize<ComponentRef>(element);
            if (reference == null || !reference.IsValid(out _))
            {
                result.Errors.Add("component_invalid");
                return;
            }

            var component = FindComponent(go, reference);
            if (component == null)
            {
                result.Errors.Add("component_not_found");
                return;
            }

            result.Targets.Add(ComponentTarget(component));
            result.Effects.Add("modify component '" + Bound(component.GetType().Name) + "' on GameObject '" + Bound(go.name) + "'");
        }

        private static void InspectComponentDestroy(AuthoringInvocation invocation, UnityPilotInspection result)
        {
            var go = ResolveRequiredGameObject(invocation, result, "gameObjectRef", "GameObject");
            if (go == null)
                return;

            if (!TryGetArgument(invocation.Arguments, "destroyComponentRefs", out var element)
                || element.ValueKind != JsonValueKind.Array)
            {
                result.Errors.Add("component_missing");
                return;
            }

            var references = Deserialize<ComponentRefList>(element);
            if (references == null || references.Count == 0)
            {
                result.Errors.Add("component_missing");
                return;
            }

            var count = 0;
            foreach (var reference in references)
            {
                if (reference == null || !reference.IsValid(out _))
                {
                    result.Errors.Add("component_invalid");
                    continue;
                }
                var component = FindComponent(go, reference);
                if (component == null)
                {
                    result.Errors.Add("component_not_found");
                    continue;
                }
                count++;
                if (result.Targets.Count < MaxListedTargets)
                    result.Targets.Add(ComponentTarget(component));
            }
            result.Effects.Add("destroy " + count + " component(s) on GameObject '" + Bound(go.name) + "'");
        }

        private static void InspectSceneAsset(
            AuthoringInvocation invocation,
            UnityPilotInspection result,
            bool requireOpened,
            string effect)
        {
            if (!TryGetArgument(invocation.Arguments, "sceneRef", out var element)
                || element.ValueKind != JsonValueKind.Object)
            {
                result.Errors.Add("target_missing");
                return;
            }

            var reference = Deserialize<AssetObjectRef>(element);
            var sceneAsset = reference == null ? null : reference.FindAssetObject<SceneAsset>();
            if (sceneAsset == null)
            {
                result.Errors.Add("scene_asset_not_found");
                return;
            }

            var assetPath = AssetDatabase.GetAssetPath(sceneAsset);
            var guid = AssetDatabase.AssetPathToGUID(assetPath);
            if (requireOpened)
            {
                var opened = UnityEngine.SceneManagement.SceneManager.GetSceneByPath(assetPath);
                if (!opened.IsValid())
                    opened = UnityEngine.SceneManagement.SceneManager.GetSceneByName(sceneAsset.name);
                if (!opened.IsValid() || !opened.isLoaded)
                    result.Errors.Add("scene_not_opened");
            }

            result.Targets.Add(new AuthoringTargetSummary
            {
                Kind = "SceneAsset",
                Name = Bound(sceneAsset.name),
                RelativePath = SafeProjectRelative(assetPath),
                Fingerprint = Hash("scene-asset|" + assetPath + "|" + guid),
            });
            result.Effects.Add(effect + " '" + Bound(sceneAsset.name) + "'");
        }

        private static void InspectSceneCreate(AuthoringInvocation invocation, UnityPilotInspection result)
        {
            if (!invocation.PathBindings.TryGetValue("path", out var binding))
            {
                result.Errors.Add("path_missing");
                return;
            }

            var relative = binding.RelativePath;
            if (!relative.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                result.Errors.Add("scene_extension");
            // Creating over an existing scene file is an overwrite, which
            // the pilot never performs implicitly.
            if (binding.Resolution.Exists)
                result.Errors.Add("scene_exists");

            result.Targets.Add(new AuthoringTargetSummary
            {
                Kind = "SceneFile",
                Name = Bound(System.IO.Path.GetFileName(relative)),
                RelativePath = relative,
                Fingerprint = Hash("scene-file|" + binding.CanonicalPath + "|exists=" + binding.Resolution.Exists),
            });
            result.Effects.Add("create scene file '" + relative + "'");
        }

        private static void InspectSceneSave(AuthoringInvocation invocation, UnityPilotInspection result)
        {
            TryGetString(invocation.Arguments, "openedSceneName", out var openedSceneName);
            var scene = string.IsNullOrEmpty(openedSceneName)
                ? EditorSceneManager.GetActiveScene()
                : SceneUtils.GetAllOpenedScenes().FirstOrDefault(candidate => candidate.name == openedSceneName);
            if (!scene.IsValid())
            {
                result.Errors.Add("scene_not_found");
                return;
            }

            string? destination = null;
            if (invocation.PathBindings.TryGetValue("path", out var binding))
            {
                destination = binding.RelativePath;
                if (!destination.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                    result.Errors.Add("scene_extension");
            }
            else if (!string.IsNullOrEmpty(scene.path))
            {
                destination = SafeProjectRelative(scene.path);
            }

            if (string.IsNullOrEmpty(destination))
                result.Errors.Add("scene_path_missing");

            result.Targets.Add(new AuthoringTargetSummary
            {
                Kind = "Scene",
                Name = Bound(scene.name),
                RelativePath = destination,
                Fingerprint = Hash("scene|" + scene.name + "|" + scene.path + "|" + scene.rootCount + "|" + destination),
            });
            result.Effects.Add("save scene '" + Bound(scene.name) + "' to '" + (destination ?? "?") + "'");
        }

        private static void InspectSceneUnload(AuthoringInvocation invocation, UnityPilotInspection result)
        {
            TryGetString(invocation.Arguments, "name", out var name);
            if (string.IsNullOrEmpty(name))
            {
                result.Errors.Add("target_missing");
                return;
            }

            var scene = SceneUtils.GetAllOpenedScenes().FirstOrDefault(candidate => candidate.name == name);
            if (!scene.IsValid())
            {
                result.Errors.Add("scene_not_found");
                return;
            }

            result.Targets.Add(new AuthoringTargetSummary
            {
                Kind = "Scene",
                Name = Bound(scene.name),
                RelativePath = SafeProjectRelative(scene.path),
                Fingerprint = Hash("scene|" + scene.name + "|" + scene.path + "|" + scene.rootCount),
            });
            result.Effects.Add("unload scene '" + Bound(scene.name) + "'");
        }

        /// <summary>
        /// Fallback for pilot-attributed tools without a dedicated inspector:
        /// the binding still covers every reference-like argument.
        /// </summary>
        private static void InspectArgumentTargets(AuthoringInvocation invocation, UnityPilotInspection result)
        {
            foreach (var argument in invocation.Arguments)
            {
                if (!LooksLikeTarget(argument.Key) && !string.Equals(argument.Key, "path", StringComparison.OrdinalIgnoreCase))
                    continue;
                result.Targets.Add(new AuthoringTargetSummary
                {
                    Kind = argument.Key,
                    Name = DisplayValue(argument.Value),
                    Fingerprint = AuthoringConfirmationBinding.ComputeArgumentsHash(
                        new Dictionary<string, JsonElement> { [argument.Key] = argument.Value.Clone() }),
                });
            }
            var kind = invocation.Descriptor?.MutationKind ?? AuthoringMutationKind.Unknown;
            result.Effects.Add(kind == AuthoringMutationKind.Unknown ? "authoring change" : kind.ToString().ToLowerInvariant());
        }

        private static GameObject? ResolveRequiredGameObject(
            AuthoringInvocation invocation,
            UnityPilotInspection result,
            string argumentName,
            string kind)
        {
            if (!TryGetArgument(invocation.Arguments, argumentName, out var element)
                || element.ValueKind != JsonValueKind.Object)
            {
                result.Errors.Add("target_missing");
                return null;
            }

            var reference = Deserialize<GameObjectRef>(element);
            if (reference == null || !reference.IsValid(out _))
            {
                result.Errors.Add("target_invalid");
                return null;
            }

            var go = GameObjectUtils.FindBy(reference, out _);
            if (go == null)
            {
                result.Errors.Add("target_not_found");
                return null;
            }

            result.Targets.Add(GameObjectTarget(go, kind));
            return go;
        }

        private static UnityEngine.Component? FindComponent(GameObject go, ComponentRef reference)
        {
            var components = go.GetComponents<UnityEngine.Component>();
            for (var index = 0; index < components.Length; index++)
            {
                var component = components[index];
                if (component != null && reference.Matches(component, index))
                    return component;
            }
            return null;
        }

        private static AuthoringTargetSummary GameObjectTarget(GameObject go, string kind)
        {
            var components = go.GetComponents<UnityEngine.Component>();
            var componentTypes = string.Join(",", components.Select(component => component == null ? "missing" : component.GetType().FullName));
            var path = go.GetPath();
            var scene = go.scene.IsValid() ? go.scene.path + "|" + go.scene.name : "no-scene";
            return new AuthoringTargetSummary
            {
                Kind = kind,
                Name = Bound(go.name),
                RelativePath = SafeHierarchyPath(path),
                Fingerprint = Hash(
                    "gameobject|" + StableId(go)
                    + "|" + path
                    + "|" + scene
                    + "|" + go.transform.childCount
                    + "|" + componentTypes
                    + "|" + go.activeSelf),
            };
        }

        private static AuthoringTargetSummary ComponentTarget(UnityEngine.Component component)
        {
            var go = component.gameObject;
            var index = Array.IndexOf(go.GetComponents<UnityEngine.Component>(), component);
            return new AuthoringTargetSummary
            {
                Kind = "Component",
                Name = Bound(component.GetType().FullName),
                RelativePath = SafeHierarchyPath(go.GetPath()),
                Fingerprint = Hash(
                    "component|" + StableId(component)
                    + "|" + component.GetType().AssemblyQualifiedName
                    + "|" + index
                    + "|" + StableId(go)),
            };
        }

        /// <summary>Identity of the active scene or open prefab stage.</summary>
        private static string ContainerFingerprint()
        {
            var stage = UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null)
                return "prefab-stage|" + stage.assetPath;
            var scene = EditorSceneManager.GetActiveScene();
            return "scene|" + scene.path + "|" + scene.name;
        }

        private static string SafeSceneDisplay(UnityEngine.SceneManagement.Scene scene)
            => scene.IsValid() ? (SafeProjectRelative(scene.path) ?? Bound(scene.name) ?? "untitled") : "none";

        private static string StableId(UnityEngine.Object value)
        {
#if UNITY_6000_5_OR_NEWER
            return value.GetEntityId().ToString();
#else
            return value.GetInstanceID().ToString(System.Globalization.CultureInfo.InvariantCulture);
#endif
        }

        private static string Hash(string material)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(material));
            var builder = new System.Text.StringBuilder(bytes.Length * 2);
            foreach (var value in bytes)
                builder.Append(value.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
            return "sha256-" + builder;
        }

        private static T? Deserialize<T>(JsonElement element) where T : class
        {
            try
            {
                var reflector = UnityCopilotPluginEditor.HasInstance
                    ? UnityCopilotPluginEditor.Instance.Reflector
                    : null;
                if (reflector != null)
                    return reflector.JsonSerializer.Deserialize<T>(reflector, element);
                return System.Text.Json.JsonSerializer.Deserialize<T>(element.GetRawText());
            }
            catch
            {
                return null;
            }
        }

        private static bool TryGetArgument(
            IReadOnlyDictionary<string, JsonElement> arguments,
            string name,
            out JsonElement value)
        {
            if (arguments.TryGetValue(name, out value))
                return true;
            foreach (var pair in arguments)
            {
                if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = pair.Value;
                    return true;
                }
            }
            value = default;
            return false;
        }

        private static bool TryGetString(
            IReadOnlyDictionary<string, JsonElement> arguments,
            string name,
            out string? value)
        {
            value = null;
            if (TryGetArgument(arguments, name, out var element) && element.ValueKind == JsonValueKind.String)
            {
                value = element.GetString();
                return true;
            }
            return false;
        }

        private static bool LooksLikeTarget(string name)
            => name.EndsWith("Ref", StringComparison.Ordinal)
                || name.EndsWith("Refs", StringComparison.Ordinal)
                || name.IndexOf("component", StringComparison.OrdinalIgnoreCase) >= 0;

        private static string? DisplayValue(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.String)
                return Bound(value.GetString());
            if (value.ValueKind == JsonValueKind.Object)
            {
                foreach (var name in new[] { "path", "name", "assetPath", "assetGuid", "instanceID", "entityId" })
                {
                    foreach (var property in value.EnumerateObject())
                    {
                        if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                            return DisplayValue(property.Value);
                    }
                }
            }
            return null;
        }

        private static string? SafeHierarchyPath(string? path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            var trimmed = path!.Replace('\\', '/').TrimStart('/');
            return trimmed.Length == 0 ? null : Bound(trimmed);
        }

        public static string? SafeProjectRelative(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            var dataPath = Application.dataPath.Replace('\\', '/');
            var normalized = path!.Replace('\\', '/');
            if (normalized.StartsWith(dataPath + "/", StringComparison.OrdinalIgnoreCase))
                return "Assets/" + normalized.Substring(dataPath.Length + 1);
            if (normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                return normalized;
            return null;
        }

        private static string? Bound(string? value)
            => value == null ? null : value.Length <= 160 ? value : value.Substring(0, 160);
    }

    /// <summary>
    /// Batch-level validator/planner. It inspects every child registration
    /// through the existing tool manager, but never invokes a child runner.
    /// The parent batch therefore cannot obtain a plan for an opaque or
    /// unsupported child and then execute it as if it were safe.
    /// </summary>
    public sealed class UnityBatchAuthoringValidator : IAuthoringValidator
    {
        public AuthoringValidationResult Validate(AuthoringInvocation invocation)
        {
            if (invocation == null)
                throw new ArgumentNullException(nameof(invocation));

            var errors = UnityBatchAuthoringPlanning.Validate(invocation);
            return new AuthoringValidationResult
            {
                Valid = errors.Count == 0,
                FailureCode = errors.Count == 0 ? null : errors[0],
                Details = errors.Count == 0
                    ? null
                    : new System.Text.Json.Nodes.JsonObject { ["reason"] = errors[0] },
            };
        }
    }

    public sealed class UnityBatchAuthoringPlanner : IAuthoringPlanner
    {
        public AuthoringPlanSummary Plan(AuthoringInvocation invocation)
        {
            if (invocation == null)
                throw new ArgumentNullException(nameof(invocation));
            return UnityBatchAuthoringPlanning.Plan(invocation);
        }
    }

    internal static class UnityBatchAuthoringPlanning
    {
        public static List<string> Validate(AuthoringInvocation invocation)
        {
            var errors = new List<string>();
            if (!TryReadCommands(invocation, out var commands, out var error))
            {
                errors.Add(error ?? "validation_failed");
                return errors;
            }

            if (commands.Count > Tool_Batch.HardMaxCommands)
            {
                errors.Add("validation_failed");
                return errors;
            }

            for (var index = 0; index < commands.Count; index++)
            {
                var command = commands[index];
                var runner = FindRunner(command.Tool);
                if (runner == null)
                {
                    errors.Add("safety_unsupported");
                    continue;
                }

                if (string.Equals(command.Tool, Tool_Batch.BatchExecuteToolId, StringComparison.Ordinal))
                {
                    errors.Add("safety_unsupported");
                    continue;
                }

                _ = AuthoringSafetyPolicy.Classify(runner);
            }

            return errors;
        }

        public static AuthoringPlanSummary Plan(AuthoringInvocation invocation)
        {
            if (!TryReadCommands(invocation, out var commands, out var error))
                throw new ToolCallControlException(
                    ToolCallErrorCodes.InvalidControl,
                    error ?? "Batch commands are invalid.");

            if (commands.Count > Tool_Batch.HardMaxCommands)
                throw new ToolCallControlException(
                    ToolCallErrorCodes.InvalidControl,
                    "The batch contains too many commands.",
                    details: new System.Text.Json.Nodes.JsonObject
                    {
                        ["reason"] = "max_commands",
                    });

            var targets = new List<AuthoringTargetSummary>();
            var effects = new List<string>();
            var childRecords = new List<AuthoringChildPlanSummary>();
            var aggregateUndo = AuthoringUndoLevel.Full;
            var supportsSharedTransaction = true;
            var hasMutatingChild = false;
            var policy = AuthoringSafetyPolicyContext.Current;
            if (policy == null)
            {
                throw new ToolCallControlException(
                    ToolCallErrorCodes.SafetyUnsupported,
                    "Batch planning requires the live authoring safety policy.",
                    details: new System.Text.Json.Nodes.JsonObject { ["reason"] = "policy_scope_missing" });
            }

            var existingChildren = invocation.PlanBeingVerified?.ChildRecords;
            if (existingChildren != null && existingChildren.Count != commands.Count)
            {
                throw new ToolCallControlException(
                    ToolCallErrorCodes.ConfirmationStale,
                    "The batch child confirmation records no longer match the command list.");
            }

            for (var index = 0; index < commands.Count; index++)
            {
                var command = commands[index];
                var runner = FindRunner(command.Tool);
                if (runner == null
                    || string.Equals(command.Tool, Tool_Batch.BatchExecuteToolId, StringComparison.Ordinal))
                {
                    throw new ToolCallControlException(
                        ToolCallErrorCodes.SafetyUnsupported,
                        "A batch child does not expose a supported authoring capability.",
                        details: new System.Text.Json.Nodes.JsonObject { ["reason"] = "child_capability_missing" });
                }

                var childContext = ToolCallContextNormalizer.DeriveChild(
                    invocation.Context,
                    new ToolCallControl
                    {
                        CallId = "batch-plan-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    },
                    requestId: "batch-plan-request-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    cancellationToken: invocation.Context.CancellationToken);
                var childInvocation = policy.PrepareInvocation(new AuthoringInvocation(
                    childContext,
                    runner.Name,
                    command.Parameters,
                    runner));
                var descriptor = childInvocation.Descriptor;
                var risk = AuthoringSafetyPolicy.Classify(
                    childInvocation.Runner.ReadOnlyHint,
                    childInvocation.Runner.DestructiveHint,
                    descriptor);
                if (descriptor != null
                    && ((descriptor.SupportsPlanning && descriptor.Planner == null)
                        || (descriptor.SupportsValidation && descriptor.Validator == null
                            && descriptor.Planner == null)))
                {
                    throw new ToolCallControlException(
                        ToolCallErrorCodes.SafetyUnsupported,
                        "A batch child has inconsistent authoring capability data.",
                        details: new System.Text.Json.Nodes.JsonObject
                        {
                            ["reason"] = "binding_unavailable",
                            ["stage"] = "declared_adapter_unavailable",
                        });
                }

                AuthoringValidationResult? childValidation = null;
                if (descriptor?.Validator != null)
                {
                    childValidation = descriptor.Validator.Validate(childInvocation);
                    if (childValidation == null || !childValidation.Valid)
                    {
                        var failureCode = childValidation?.FailureCode;
                        var safeFailureCode = string.IsNullOrWhiteSpace(failureCode)
                            ? "validation_failed"
                            : failureCode ?? "validation_failed";
                        throw new ToolCallControlException(
                            safeFailureCode,
                            "A batch child did not pass validation.");
                    }
                }

                var childPlan = descriptor?.Planner?.Plan(childInvocation);
                if (descriptor?.Planner != null && childPlan == null)
                {
                    throw new ToolCallControlException(
                        ToolCallErrorCodes.SafetyUnsupported,
                        "A batch child planner returned no plan.",
                        details: new System.Text.Json.Nodes.JsonObject
                        {
                            ["reason"] = "binding_unavailable",
                            ["stage"] = "planner_returned_null",
                        });
                }

                var validationTargets = childValidation?.Targets == null
                    ? Array.Empty<AuthoringTargetSummary>()
                    : childValidation.Targets.Where(target => target != null).Take(8)
                        .Select(target => target.CloneSafe()).ToArray();
                var plannedTargets = childPlan?.Targets == null
                    ? Array.Empty<AuthoringTargetSummary>()
                    : childPlan.Targets.Where(target => target != null).Take(8)
                        .Select(target => target.CloneSafe()).ToArray();
                IReadOnlyList<AuthoringTargetSummary> childTargets = plannedTargets.Length == 0
                    ? validationTargets
                    : plannedTargets;
                var plannedEffects = childPlan?.PredictedEffects == null
                    ? Array.Empty<string>()
                    : childPlan.PredictedEffects.Where(effect => !string.IsNullOrWhiteSpace(effect)).Take(8)
                        .Select(effect => effect.Length <= 160 ? effect : effect.Substring(0, 160)).ToArray();
                IReadOnlyList<string> childEffects = plannedEffects.Length > 0
                    ? plannedEffects
                    : new[]
                    {
                        descriptor == null || descriptor.IsOpaque
                            ? "undeclared"
                            : descriptor.MutationKind == AuthoringMutationKind.Unknown
                                ? "authoring change"
                                : descriptor.MutationKind.ToString().ToLowerInvariant(),
                    };

                AuthoringChildPlanSummary? existingAuthority = null;
                if (existingChildren != null)
                {
                    existingAuthority = existingChildren[index];
                    if (existingAuthority == null
                        || existingAuthority.Index != index
                        || !string.Equals(existingAuthority.ToolName, runner.Name, StringComparison.Ordinal))
                    {
                        throw new ToolCallControlException(
                            ToolCallErrorCodes.ConfirmationStale,
                            "The batch child confirmation records no longer match the command list.");
                    }
                }

                var childRecord = policy.CreateAggregateChildRecord(
                    index,
                    childInvocation,
                    childValidation,
                    childPlan,
                    childTargets,
                    childEffects,
                    existingAuthority);
                childRecords.Add(childRecord);

                if (risk != AuthoringRiskLevel.Read)
                {
                    hasMutatingChild = true;
                    aggregateUndo = (AuthoringUndoLevel)Math.Min(
                        (int)aggregateUndo,
                        (int)childRecord.UndoLevel);
                    supportsSharedTransaction &= childRecord.SupportsSharedTransaction;
                }

                targets.Add(new AuthoringTargetSummary
                {
                    Kind = "batch-child",
                    Name = runner.Name,
                    Fingerprint = AuthoringConfirmationBinding.ComputeArgumentsHash(
                        new Dictionary<string, JsonElement>
                        {
                            ["index"] = System.Text.Json.JsonSerializer.SerializeToElement(childRecord.Index),
                            ["tool"] = System.Text.Json.JsonSerializer.SerializeToElement(childRecord.ToolName),
                            ["argumentsHash"] = System.Text.Json.JsonSerializer.SerializeToElement(childRecord.ArgumentsHash),
                            ["risk"] = System.Text.Json.JsonSerializer.SerializeToElement(childRecord.Risk),
                            ["undo"] = System.Text.Json.JsonSerializer.SerializeToElement(childRecord.Undo),
                            ["planLevel"] = System.Text.Json.JsonSerializer.SerializeToElement(childRecord.PlanLevel),
                        }),
                });
                targets.AddRange(childRecord.Targets.Select(target => target.CloneSafe()));
                effects.AddRange(childRecord.PredictedEffects);
            }

            if (effects.Count == 0)
                effects.Add("authoring change");

            return new AuthoringPlanSummary
            {
                UndoLevel = aggregateUndo,
                HasUndoOverride = true,
                SupportsSharedTransaction = hasMutatingChild && supportsSharedTransaction,
                Targets = targets.Take(32).Select(target => target.CloneSafe()).ToArray(),
                PredictedEffects = effects.Take(32).ToArray(),
                ChildRecords = childRecords.Take(Tool_Batch.HardMaxCommands)
                    .Select(child => child.CloneBounded()).ToArray(),
                BeforeSummary = new System.Text.Json.Nodes.JsonObject
                {
                    ["childCount"] = commands.Count,
                },
                AfterSummary = new System.Text.Json.Nodes.JsonObject
                {
                    ["childCount"] = commands.Count,
                    ["predictedEffects"] = effects.Take(32).Aggregate(
                        new System.Text.Json.Nodes.JsonArray(),
                        (array, effect) =>
                        {
                            array.Add(effect);
                            return array;
                        }),
                },
            };
        }

        private sealed class ParsedCommand
        {
            public string Tool { get; }
            public Dictionary<string, JsonElement> Parameters { get; }

            public ParsedCommand(string tool, Dictionary<string, JsonElement> parameters)
            {
                Tool = tool;
                Parameters = parameters;
            }
        }

        private static bool TryReadCommands(
            AuthoringInvocation invocation,
            out List<ParsedCommand> commands,
            out string? error)
        {
            commands = new List<ParsedCommand>();
            error = null;
            if (!TryGetArgument(invocation.Arguments, "commands", out var value)
                || value.ValueKind != JsonValueKind.Array)
            {
                error = "validation_failed";
                return false;
            }

            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    error = "validation_failed";
                    return false;
                }
                if (!TryGetProperty(item, "tool", out var toolValue)
                    || toolValue.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(toolValue.GetString()))
                {
                    error = "validation_failed";
                    return false;
                }

                var parameters = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                if (TryGetProperty(item, "params", out var paramsValue)
                    && paramsValue.ValueKind != JsonValueKind.Null)
                {
                    if (paramsValue.ValueKind != JsonValueKind.Object)
                    {
                        error = "validation_failed";
                        return false;
                    }
                    foreach (var property in paramsValue.EnumerateObject())
                        parameters[property.Name] = property.Value.Clone();
                }

                commands.Add(new ParsedCommand(toolValue.GetString()!, parameters));
            }

            if (commands.Count == 0)
            {
                error = "validation_failed";
                return false;
            }
            return true;
        }

        private static IRunTool? FindRunner(string name)
        {
            if (!UnityCopilotPluginEditor.HasInstance)
                return null;
            var manager = UnityCopilotPluginEditor.Instance.Tools;
            if (manager == null)
                return null;
            return manager.GetAllTools()
                .FirstOrDefault(item => item != null && string.Equals(item.Name, name, StringComparison.Ordinal));
        }

        private static bool TryGetArgument(
            IReadOnlyDictionary<string, JsonElement> arguments,
            string name,
            out JsonElement value)
        {
            if (arguments.TryGetValue(name, out value)) return true;
            foreach (var pair in arguments)
            {
                if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = pair.Value;
                    return true;
                }
            }
            value = default;
            return false;
        }

        private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
            value = default;
            return false;
        }
    }

    /// <summary>
    /// Read-only planner for the pilot. It performs the same live target
    /// inspection as the validator and adds predicted effects plus bounded
    /// before/after summaries. A plan for an invalid call is refused rather
    /// than issued, so a confirmation token can only ever describe an action
    /// whose targets were actually observed.
    /// </summary>
    public sealed class UnityPilotAuthoringPlanner : IAuthoringPlanner
    {
        public AuthoringPlanSummary Plan(AuthoringInvocation invocation)
        {
            if (invocation == null)
                throw new ArgumentNullException(nameof(invocation));

            var inspection = UnityPilotAuthoringInspection.Inspect(invocation);
            if (inspection.Errors.Count > 0)
            {
                throw new ToolCallControlException(
                    "validation_failed",
                    "The authoring call did not pass validation.",
                    details: new System.Text.Json.Nodes.JsonObject
                    {
                        ["reason"] = inspection.Errors[0],
                        ["errorCount"] = inspection.Errors.Count,
                    });
            }

            var kind = invocation.Descriptor?.MutationKind ?? AuthoringMutationKind.Unknown;
            var effects = inspection.Effects.Count == 0
                ? new List<string> { kind == AuthoringMutationKind.Unknown ? "authoring change" : kind.ToString().ToLowerInvariant() }
                : inspection.Effects;
            return new AuthoringPlanSummary
            {
                Targets = inspection.Targets.Take(32).ToArray(),
                PredictedEffects = effects.Take(32).ToArray(),
                BeforeSummary = inspection.Before,
                AfterSummary = inspection.After,
            };
        }
    }

    /// <summary>Unity Undo-backed transaction factory attached to pilot runners.</summary>
    public sealed class UnityAuthoringTransactionFactory : IAuthoringTransactionFactory
    {
        public IAuthoringTransaction Begin(AuthoringInvocation invocation)
        {
            if (invocation == null)
                throw new ArgumentNullException(nameof(invocation));

            return BeginStandalone(
                "MCP: " + invocation.Name,
                invocation.EffectiveUndoLevel
                    ?? invocation.Descriptor?.UndoLevel
                    ?? AuthoringUndoLevel.Partial);
        }

        /// <summary>
        /// Opens one named Unity Undo group and returns the transaction that
        /// owns it. Complete collapses the group to one user-visible step;
        /// Abort reverts only this group (never unrelated earlier history).
        /// </summary>
        public static IAuthoringTransaction BeginStandalone(string label, AuthoringUndoLevel advertisedUndo)
        {
            if (string.IsNullOrWhiteSpace(label))
                throw new ArgumentException("Transaction label must be non-empty.", nameof(label));

            // The middleware may run on the transport thread; every Undo API
            // call is marshalled to the main thread (inline when already there).
            var group = MainThread.Instance.Run(() =>
            {
                Undo.IncrementCurrentGroup();
                var current = Undo.GetCurrentGroup();
                Undo.SetCurrentGroupName(label);
                return current;
            });

            return new UnityAuthoringTransaction(
                label,
                advertisedUndo,
                group,
                recordCreated: value => MainThread.Instance.Run(() => Undo.RegisterCreatedObjectUndo(
                    RequireObject(value), label)),
                recordModified: (value, completeSnapshot) => MainThread.Instance.Run(() =>
                {
                    var target = RequireObject(value);
                    if (completeSnapshot)
                        Undo.RegisterCompleteObjectUndo(target, label);
                    else
                        Undo.RecordObject(target, label);
                }),
                recordDeleted: value => MainThread.Instance.Run(() => Undo.RegisterCompleteObjectUndo(
                    RequireObject(value), label)),
                complete: () => MainThread.Instance.Run(() => Undo.CollapseUndoOperations(group)),
                abort: () => MainThread.Instance.Run(() => Undo.RevertAllDownToGroup(group)));
        }

        /// <summary>
        /// Test/tooling helper: opens a standalone transaction, installs it as
        /// the ambient scope, and completes it on dispose. Production calls
        /// always receive their scope from <see cref="AuthoringSafetyMiddleware"/>.
        /// </summary>
        public static IDisposable BeginStandaloneScope(string label, AuthoringUndoLevel advertisedUndo = AuthoringUndoLevel.Full)
        {
            var transaction = BeginStandalone(label, advertisedUndo);
            var scope = AuthoringTransactionScope.Push(transaction);
            return new StandaloneScope(transaction, scope);
        }

        private sealed class StandaloneScope : IDisposable
        {
            private readonly IAuthoringTransaction _transaction;
            private readonly IDisposable _scope;
            private int _disposed;

            public StandaloneScope(IAuthoringTransaction transaction, IDisposable scope)
            {
                _transaction = transaction;
                _scope = scope;
            }

            public void Dispose()
            {
                if (System.Threading.Interlocked.Exchange(ref _disposed, 1) != 0)
                    return;
                try
                {
                    _transaction.Complete();
                }
                finally
                {
                    _scope.Dispose();
                    _transaction.Dispose();
                }
            }
        }

        private static UnityEngine.Object RequireObject(object value)
            => value as UnityEngine.Object
                ?? throw new InvalidOperationException("The authoring target is not a Unity object.");
    }

    /// <summary>
    /// Unity-aware transaction: affected-object summaries carry the object's
    /// type and name instead of the generic <c>ToString()</c> fallback, and
    /// hierarchy paths are reported as safe relative display values.
    /// </summary>
    public sealed class UnityAuthoringTransaction : AuthoringTransaction
    {
        public UnityAuthoringTransaction(
            string groupLabel,
            AuthoringUndoLevel advertisedUndo,
            int groupId,
            Action<object>? recordCreated,
            Action<object, bool>? recordModified,
            Action<object>? recordDeleted,
            Action? complete,
            Action? abort)
            : base(groupLabel, advertisedUndo, groupId, recordCreated, recordModified, recordDeleted, complete, abort)
        {
        }

        private readonly HashSet<string> _reported = new HashSet<string>(StringComparer.Ordinal);

        protected override void AddAffected(object value)
        {
            if (value is UnityEngine.Object unityObject && unityObject != null)
            {
                // One object may be recorded more than once inside a call
                // (for example RegisterCreatedObjectUndo followed by a
                // complete snapshot); report it once.
#if UNITY_6000_5_OR_NEWER
                var objectId = unityObject.GetEntityId().ToString();
#else
                var objectId = unityObject.GetInstanceID().ToString(System.Globalization.CultureInfo.InvariantCulture);
#endif
                var path = value is GameObject go
                    ? go.GetPath()
                    : value is UnityEngine.Component component && component != null
                        ? component.gameObject.GetPath()
                        : null;
                var summary = new AuthoringAffectedObjectSummary
                {
                    Kind = value.GetType().Name,
                    Name = unityObject.name,
                    RelativePath = string.IsNullOrEmpty(path) ? null : path!.Replace('\\', '/').TrimStart('/'),
                };
                if (!_reported.Add(objectId))
                {
                    AddAffectedEvent(summary);
                    return;
                }

                AddAffectedSummary(summary);
                return;
            }

            base.AddAffected(value);
        }
    }

    /// <summary>
    /// Small helper used by pilot tools.  Every mutation records through the
    /// ambient transaction before changing Unity state.
    /// </summary>
    public static class UnityAuthoringUndo
    {
        /// <summary>
        /// Fails closed before the first mutation of a pilot tool body when
        /// the call did not arrive through the policy pipeline. An explicit
        /// ambient transaction (opened by the middleware, or deliberately by
        /// in-process tooling/tests) is accepted; a bare reflected runner
        /// call is not, so no object is created and then orphaned by a later
        /// missing-transaction failure.
        /// </summary>
        public static void RequireAuthoringScope()
        {
            if (AuthoringTransactionScope.Current != null)
                return;
            AuthoringPolicyApproval.Require();
        }

        public static void RecordCreated(UnityEngine.Object value)
        {
            var transaction = AuthoringTransactionScope.Current;
            if (transaction == null)
                throw new InvalidOperationException("An authoring transaction is required.");
            transaction.RecordCreated(value);
        }

        public static void RecordModified(UnityEngine.Object value, bool completeSnapshot = false)
        {
            var transaction = AuthoringTransactionScope.Current;
            if (transaction == null)
                throw new InvalidOperationException("An authoring transaction is required.");
            transaction.RecordModified(value, completeSnapshot);
        }

        public static void Destroy(UnityEngine.Object value)
        {
            var transaction = AuthoringTransactionScope.Current;
            if (transaction == null)
                throw new InvalidOperationException("An authoring transaction is required.");
            transaction.RecordDeleted(value);
            Undo.DestroyObjectImmediate(value);
            transaction.MarkMutated();
        }

        /// <summary>
        /// Undo-aware reparenting. <c>Undo.RecordObject</c> on a Transform
        /// does not capture hierarchy changes; Unity's
        /// <c>Undo.SetTransformParent</c> records the parent change inside
        /// the current group.
        /// </summary>
        public static void SetParent(Transform target, Transform? parent, bool worldPositionStays)
        {
            var transaction = AuthoringTransactionScope.Current;
            if (transaction == null)
                throw new InvalidOperationException("An authoring transaction is required.");
            if (target == null)
                throw new ArgumentNullException(nameof(target));
            transaction.RecordHostRegistered(target);
            Undo.SetTransformParent(target, parent, worldPositionStays, "MCP: set parent");
            transaction.MarkMutated();
        }

        /// <summary>
        /// Undo-aware component creation. <c>Undo.AddComponent</c> creates
        /// and registers the component in one step; a second
        /// <c>RegisterCreatedObjectUndo</c> would duplicate the record.
        /// </summary>
        public static UnityEngine.Component? AddComponent(GameObject target, Type componentType)
        {
            var transaction = AuthoringTransactionScope.Current;
            if (transaction == null)
                throw new InvalidOperationException("An authoring transaction is required.");
            if (target == null)
                throw new ArgumentNullException(nameof(target));
            if (componentType == null)
                throw new ArgumentNullException(nameof(componentType));

            var component = Undo.AddComponent(target, componentType);
            if (component == null)
                return null;
            transaction.RecordHostRegistered(component);
            transaction.MarkMutated();
            return component;
        }

        /// <summary>
        /// Tracks an object that a Unity API already registered with Undo
        /// inside the current group (for example pasteboard duplication).
        /// </summary>
        public static void RecordHostRegistered(UnityEngine.Object value)
        {
            var transaction = AuthoringTransactionScope.Current;
            if (transaction == null)
                throw new InvalidOperationException("An authoring transaction is required.");
            transaction.RecordHostRegistered(value);
        }

        public static void MarkMutated()
            => AuthoringTransactionScope.Current?.MarkMutated();
    }
}
