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
using System.ComponentModel;
using System.IO;
using com.IvanMurzak.McpPlugin;
using com.IvanMurzak.ReflectorNet.Utils;
using com.AtelierAI.Unity.Copilot.Editor.Utils;
using com.AtelierAI.Unity.Copilot.Runtime.Extensions;
using AIGD;
using UnityEditor;
using UnityEngine;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    public partial class Tool_Graphics
    {
        public const string GraphicsVolumeCreateToolId = "graphics-volume-create";

        [McpPluginTool
        (
            GraphicsVolumeCreateToolId,
            Title = "Graphics / Volume / Create",
            DestructiveHint = true
        )]
        [McpPluginSkillDescription("Create a `UnityEngine.Rendering.Volume` GameObject in the active scene with a " +
            "`VolumeProfile` assigned. Either reuses an existing profile (`profileAssetPath`) or creates a fresh " +
            "blank profile at `newProfileAssetPath` (default: `Assets/Volumes/{name}_Profile.asset`). The volume's " +
            "`isGlobal`, `priority` and `weight` knobs are configurable. Returns the new GameObject reference and " +
            "the resolved profile asset path. Requires SRP Core.")]
        [McpPluginSkillBody("Creates a Volume GameObject + VolumeProfile in one call.\n\n" +
            "## Inputs\n\n" +
            "- `name` — required non-empty GameObject name.\n" +
            "- `parent` (optional) — when provided, the new GameObject is parented under this one.\n" +
            "- `position` (optional, default `(0,0,0)`) — local position of the new GameObject.\n" +
            "- `isGlobal` (default `true`) — global volumes affect the camera regardless of position.\n" +
            "- `priority` (default 0), `weight` (default 1) — Volume blend knobs.\n" +
            "- `profileAssetPath` (optional) — path to an existing `*.asset` VolumeProfile to reuse.\n" +
            "- `newProfileAssetPath` (optional) — used only when `profileAssetPath` is null. " +
            "Defaults to `Assets/Volumes/{name}_Profile.asset`; intermediate folders are created.\n\n" +
            "## Behavior\n\n" +
            "Runs on the Unity main thread. Records Undo, marks the GameObject + Volume + profile dirty, " +
            "and saves the new profile asset via `AssetDatabase.CreateAsset` + `SaveAssets`.")]
        [Description("Create a Volume GameObject with a VolumeProfile assigned. " +
            "Reuses an existing profile if profileAssetPath is set, otherwise creates a blank one.")]
        public VolumeCreateResult CreateVolume
        (
            [Description("Name of the new GameObject.")]
            string name,
            [Description("Optional parent GameObject. When null, the volume is created at scene root.")]
            GameObjectRef? parent = null,
            [Description("Optional local position. Defaults to (0,0,0).")]
            Vector3? position = null,
            [Description("Set Volume.isGlobal. Default true.")]
            bool isGlobal = true,
            [Description("Set Volume.priority. Default 0.")]
            float priority = 0f,
            [Description("Set Volume.weight in [0..1]. Default 1.")]
            float weight = 1f,
            [Description("Path to an existing VolumeProfile asset to assign. Optional — when null, a blank " +
                "profile is created at 'newProfileAssetPath'.")]
            string? profileAssetPath = null,
            [Description("Path where a fresh VolumeProfile asset will be created when 'profileAssetPath' is null. " +
                "Default 'Assets/Volumes/{name}_Profile.asset'. Intermediate folders are created.")]
            string? newProfileAssetPath = null
        )
        {
            if (string.IsNullOrEmpty(name))
                return new VolumeCreateResult { Ok = false, Error = Error.NameRequired() };

            return MainThread.Instance.Run(() =>
            {
                if (!IsSrpCoreAvailable())
                    return new VolumeCreateResult { Ok = false, Error = Error.SrpCoreNotInstalled() };

                var volumeType = GetVolumeType();
                var profileType = GetVolumeProfileType();
                if (volumeType == null || profileType == null)
                    return new VolumeCreateResult { Ok = false, Error = Error.SrpCoreNotInstalled() };

                // Resolve / create the VolumeProfile.
                UnityEngine.Object? profile = null;
                string profilePath;

                if (!string.IsNullOrEmpty(profileAssetPath))
                {
                    profile = AssetDatabase.LoadAssetAtPath(profileAssetPath, profileType);
                    if (profile == null)
                        return new VolumeCreateResult { Ok = false, Error = Error.ProfileAssetNotFound(profileAssetPath!) };
                    if (!profileType.IsAssignableFrom(profile.GetType()))
                        return new VolumeCreateResult { Ok = false, Error = Error.ProfileAssetWrongType(profileAssetPath!) };
                    profilePath = profileAssetPath!;
                }
                else
                {
                    profilePath = !string.IsNullOrEmpty(newProfileAssetPath)
                        ? newProfileAssetPath!
                        : $"Assets/Volumes/{name}_Profile.asset";

                    if (!profilePath.EndsWith(".asset", System.StringComparison.OrdinalIgnoreCase))
                        profilePath += ".asset";

                    // Create intermediate folders if missing.
                    var dir = Path.GetDirectoryName(profilePath)?.Replace('\\', '/');
                    if (!string.IsNullOrEmpty(dir) && !AssetDatabase.IsValidFolder(dir))
                    {
                        EnsureFolderRecursive(dir!);
                    }

                    // If something already lives at the path, ensure it's a profile and reuse it.
                    var existing = AssetDatabase.LoadAssetAtPath(profilePath, profileType);
                    if (existing != null && profileType.IsAssignableFrom(existing.GetType()))
                    {
                        profile = existing;
                    }
                    else
                    {
                        // VolumeProfile : ScriptableObject — create blank instance.
                        var created = ScriptableObject.CreateInstance(profileType);
                        if (created == null)
                            return new VolumeCreateResult
                            {
                                Ok = false,
                                Error = $"ScriptableObject.CreateInstance('{profileType.FullName}') returned null."
                            };
                        created.name = Path.GetFileNameWithoutExtension(profilePath);
                        AssetDatabase.CreateAsset(created, profilePath);
                        AssetDatabase.SaveAssets();
                        profile = created;
                    }
                }

                // Create the GameObject and Volume component.
                var go = new GameObject(name);
                Undo.RegisterCreatedObjectUndo(go, "Create Volume GameObject");

                if (parent != null && parent.IsValid(out _))
                {
                    var parentGo = parent.FindGameObject(out var parentErr);
                    if (parentErr != null || parentGo == null)
                    {
                        UnityEngine.Object.DestroyImmediate(go);
                        return new VolumeCreateResult
                        {
                            Ok = false,
                            Error = parentErr ?? "Parent GameObject not found."
                        };
                    }
                    go.transform.SetParent(parentGo.transform, worldPositionStays: false);
                }

                if (position.HasValue)
                    go.transform.localPosition = position.Value;

                var volume = go.AddComponent(volumeType);
                TrySetVolumeBool(volume, "isGlobal", isGlobal);
                TrySetVolumeFloat(volume, "priority", priority);
                TrySetVolumeFloat(volume, "weight", weight);
                SetSharedProfile(volume, profile);

                EditorUtility.SetDirty(volume);
                EditorUtility.SetDirty(go);
                if (profile != null) EditorUtility.SetDirty(profile);
                EditorUtils.RepaintAllEditorWindows();

                return new VolumeCreateResult
                {
                    Ok = true,
                    GameObjectPath = GetHierarchyPath(go),
                    ProfileAssetPath = profilePath,
                    Volume = new GameObjectRef(go)
                };
            });
        }

        private static void EnsureFolderRecursive(string folder)
        {
            folder = folder.Replace('\\', '/');
            if (string.IsNullOrEmpty(folder) || folder == "Assets") return;
            if (AssetDatabase.IsValidFolder(folder)) return;

            var parent = Path.GetDirectoryName(folder)?.Replace('\\', '/');
            var leaf = Path.GetFileName(folder);
            if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(leaf)) return;

            EnsureFolderRecursive(parent!);
            if (!AssetDatabase.IsValidFolder(folder))
                AssetDatabase.CreateFolder(parent!, leaf!);
        }

        public class VolumeCreateResult
        {
            [Description("True on success.")]
            public bool Ok { get; set; }

            [Description("Hierarchy path of the newly created Volume GameObject.")]
            public string? GameObjectPath { get; set; }

            [Description("Asset path of the VolumeProfile that was assigned (newly created or reused).")]
            public string? ProfileAssetPath { get; set; }

            [Description("Reference to the new Volume GameObject — pass to other graphics/gameobject tools.")]
            public GameObjectRef? Volume { get; set; }

            [Description("Error message on failure. Null on success.")]
            public string? Error { get; set; }
        }
    }
}
