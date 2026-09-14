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
using com.AtelierAI.Uco.Framework;
using com.IvanMurzak.ReflectorNet.Utils;
using com.AtelierAI.Unity.Copilot.Editor.Utils;
using UnityEditor;
using UnityEngine;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    public partial class Tool_Graphics
    {
        public const string GraphicsVolumeRemoveOverrideToolId = "graphics-volume-remove-override";

        [UcoTool
        (
            GraphicsVolumeRemoveOverrideToolId,
            Title = "Graphics / Volume / Remove Override",
            DestructiveHint = true
        )]
        [UcoSkillDescription("Remove a `VolumeComponent` override from a `VolumeProfile`. " +
            "`componentTypeName` accepts the full name or short class name. Returns Ok=false with a structured " +
            "error when SRP Core is missing, the type cannot be resolved, the profile cannot be loaded, or the " +
            "override is not currently present on the profile.")]
        [UcoSkillBody("Calls `VolumeProfile.Remove(Type)` via reflection.\n\n" +
            "## Inputs\n\n" +
            "- `profileOrVolumePath` — asset path of a VolumeProfile or scene hierarchy path of a Volume.\n" +
            "- `componentTypeName` — full or short type name of the VolumeComponent to remove.\n\n" +
            "## Behavior\n\n" +
            "Runs on the Unity main thread. Pre-checks that the override exists on the profile (so the call " +
            "returns a clear error rather than silently no-op'ing). Records Undo, marks the profile dirty, " +
            "and saves the asset.")]
        [Description("Remove a VolumeComponent override (e.g. Bloom) from a VolumeProfile.")]
        public VolumeOverrideResult RemoveOverride
        (
            [Description("Asset path of a VolumeProfile (*.asset) OR scene hierarchy path of a Volume GameObject.")]
            string profileOrVolumePath,
            [Description("Full or short type name of the VolumeComponent override to remove.")]
            string componentTypeName
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

                var existing = CallProfileTryGet(profile, componentType);
                if (existing == null)
                    return new VolumeOverrideResult
                    {
                        Ok = false,
                        ProfileAssetPath = profilePath,
                        ComponentType = componentType.FullName ?? componentTypeName,
                        Error = Error.OverrideNotPresent(componentType.FullName ?? componentTypeName)
                    };

                Undo.RecordObject(profile, $"Remove Volume Override {componentType.Name}");

                if (!CallProfileRemove(profile, componentType))
                    return new VolumeOverrideResult
                    {
                        Ok = false,
                        ProfileAssetPath = profilePath,
                        ComponentType = componentType.FullName ?? componentTypeName,
                        Error = "VolumeProfile.Remove(Type) failed via reflection."
                    };

                // Profile.Remove leaves the removed VolumeComponent as a sub-asset of the profile when
                // the profile is saved to disk. Destroy it so the profile asset doesn't accumulate
                // orphaned VolumeComponent sub-objects.
                UnityEngine.Object.DestroyImmediate(existing, allowDestroyingAssets: true);

                EditorUtility.SetDirty(profile);
                if (!string.IsNullOrEmpty(profilePath))
                    AssetDatabase.SaveAssetIfDirty(profile);
                EditorUtils.RepaintAllEditorWindows();

                return new VolumeOverrideResult
                {
                    Ok = true,
                    ProfileAssetPath = profilePath,
                    ComponentType = componentType.FullName ?? componentTypeName,
                    Active = null
                };
            });
        }
    }
}
