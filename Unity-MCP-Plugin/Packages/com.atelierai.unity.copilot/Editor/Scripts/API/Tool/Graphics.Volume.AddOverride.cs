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
using System.Reflection;
using com.IvanMurzak.McpPlugin;
using com.IvanMurzak.ReflectorNet.Utils;
using com.AtelierAI.Unity.Copilot.Editor.Utils;
using UnityEditor;
using UnityEngine;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    public partial class Tool_Graphics
    {
        public const string GraphicsVolumeAddOverrideToolId = "graphics-volume-add-override";

        [McpPluginTool
        (
            GraphicsVolumeAddOverrideToolId,
            Title = "Graphics / Volume / Add Override",
            DestructiveHint = true
        )]
        [McpPluginSkillDescription("Add a `VolumeComponent` override to a `VolumeProfile`. The profile can be " +
            "addressed either by asset path (e.g. `Assets/Volumes/MyProfile.asset`) or by the scene hierarchy " +
            "path of a Volume GameObject (in which case its `sharedProfile` is used). " +
            "`componentTypeName` accepts the full name (`UnityEngine.Rendering.Universal.Bloom`) or the short " +
            "class name (`Bloom`); URP / HDRP types resolve only when the corresponding package is installed.")]
        [McpPluginSkillBody("Calls `VolumeProfile.Add(Type, bool)` via reflection to keep the editor assembly " +
            "free of compile-time references to URP / HDRP.\n\n" +
            "## Inputs\n\n" +
            "- `profileOrVolumePath` — asset path of a `*.asset` profile, OR scene hierarchy path of a Volume.\n" +
            "- `componentTypeName` — full or short type name of the VolumeComponent class to add.\n" +
            "- `active` (default `true`) — value to set on `VolumeComponent.active` after the add.\n\n" +
            "## Behavior\n\n" +
            "Runs on the Unity main thread. Returns a structured error when:\n\n" +
            "- SRP Core is missing,\n" +
            "- the type is not found in any loaded assembly,\n" +
            "- the type is not a `VolumeComponent` subclass,\n" +
            "- the profile cannot be resolved.\n\n" +
            "Records Undo and marks the profile dirty + saves the asset.")]
        [Description("Add a VolumeComponent override (e.g. Bloom, ColorAdjustments) to a VolumeProfile. " +
            "profileOrVolumePath accepts either an asset path or a scene Volume's hierarchy path.")]
        public VolumeOverrideResult AddOverride
        (
            [Description("Asset path of a VolumeProfile (*.asset) OR scene hierarchy path of a Volume GameObject.")]
            string profileOrVolumePath,
            [Description("Full or short type name of the VolumeComponent. " +
                "Examples: 'UnityEngine.Rendering.Universal.Bloom', 'ColorAdjustments', " +
                "'UnityEngine.Rendering.HighDefinition.Tonemapping'.")]
            string componentTypeName,
            [Description("Set VolumeComponent.active after adding. Default true.")]
            bool active = true
        )
        {
            return MainThread.Instance.Run(() =>
            {
                if (!IsSrpCoreAvailable())
                    return new VolumeOverrideResult { Ok = false, Error = Error.SrpCoreNotInstalled() };

                var componentType = ResolveComponentType(componentTypeName);
                if (componentType == null)
                    return new VolumeOverrideResult
                    {
                        Ok = false,
                        ComponentType = componentTypeName,
                        Error = Error.VolumeComponentTypeNotFound(componentTypeName)
                    };

                var vcBase = GetVolumeComponentType();
                if (vcBase == null || !vcBase.IsAssignableFrom(componentType))
                    return new VolumeOverrideResult
                    {
                        Ok = false,
                        ComponentType = componentType.FullName ?? componentTypeName,
                        Error = Error.TypeIsNotVolumeComponent(componentTypeName)
                    };

                var profile = ResolveProfile(profileOrVolumePath, out var profilePath, out var resolveErr);
                if (profile == null)
                    return new VolumeOverrideResult
                    {
                        Ok = false,
                        ComponentType = componentType.FullName ?? componentTypeName,
                        Error = resolveErr ?? Error.ProfilePathRequired()
                    };

                Undo.RecordObject(profile, $"Add Volume Override {componentType.Name}");

                // If an override of this type already exists, reuse it (Add documented behavior).
                var existing = CallProfileTryGet(profile, componentType);
                UnityEngine.Object? created = existing ?? CallProfileAdd(profile, componentType, overrides: true);

                if (created == null)
                    return new VolumeOverrideResult
                    {
                        Ok = false,
                        ProfileAssetPath = profilePath,
                        ComponentType = componentType.FullName ?? componentTypeName,
                        Error = "VolumeProfile.Add(Type, bool) failed via reflection."
                    };

                // VolumeComponent.active is a public field (not property) on most SRP versions.
                var activeField = created.GetType().GetField("active",
                    BindingFlags.Public | BindingFlags.Instance);
                if (activeField != null)
                    activeField.SetValue(created, active);

                EditorUtility.SetDirty(profile);
                EditorUtility.SetDirty(created);
                if (!string.IsNullOrEmpty(profilePath))
                    AssetDatabase.SaveAssetIfDirty(profile);
                EditorUtils.RepaintAllEditorWindows();

                return new VolumeOverrideResult
                {
                    Ok = true,
                    ProfileAssetPath = profilePath,
                    ComponentType = componentType.FullName ?? componentTypeName,
                    Active = active
                };
            });
        }

        public class VolumeOverrideResult
        {
            [Description("True on success.")]
            public bool Ok { get; set; }

            [Description("Resolved profile asset path — may be null when the profile is an in-memory instance " +
                "(e.g. a Volume's sharedProfile not yet saved).")]
            public string? ProfileAssetPath { get; set; }

            [Description("Full name of the VolumeComponent type that was added / removed.")]
            public string? ComponentType { get; set; }

            [Description("VolumeComponent.active flag after the operation. Null when the override was removed.")]
            public bool? Active { get; set; }

            [Description("Error message on failure. Null on success.")]
            public string? Error { get; set; }
        }
    }
}
