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
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using com.AtelierAI.Uco.Framework;
using com.IvanMurzak.ReflectorNet.Utils;
using UnityEditor;
using UnityEngine;
using Component = UnityEngine.Component;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    public partial class Tool_Graphics
    {
        public const string GraphicsVolumeListToolId = "graphics-volume-list";

        [UcoTool
        (
            GraphicsVolumeListToolId,
            Title = "Graphics / Volume / List",
            ReadOnlyHint = true,
            IdempotentHint = true
        )]
        [UcoSkillDescription("List every `UnityEngine.Rendering.Volume` (URP/HDRP/SRP-Core post-processing " +
            "volume) in the currently opened scenes. Returns hierarchy path, isGlobal/priority/weight flags, the " +
            "asset path of the assigned `VolumeProfile` (when set), and the short class names of each override " +
            "the profile contains. Filter the result set with `sceneGlob` and/or `onlyWithProfile`.")]
        [UcoSkillBody("Read-only inventory of Volume components. Returns Ok=false with an SRP-Core-missing " +
            "error when the `com.unity.render-pipelines.core` package is not installed.\n\n" +
            "## Inputs\n\n" +
            "- `sceneGlob` (optional) — wildcard pattern (`*`/`?`) applied against the scene asset path of each " +
            "Volume's owning scene. Use to scope results to a single scene.\n" +
            "- `onlyWithProfile` (optional, default false) — when true, hides Volumes whose `sharedProfile` is null.\n\n" +
            "## Output shape\n\n" +
            "- `Volumes` — array of `VolumeInfo`, one entry per Volume component (active scenes only, includes " +
            "disabled GameObjects). Each entry's `Overrides` is the short class-name list of the profile's " +
            "components, or empty when no profile is assigned.")]
        [Description("List every UnityEngine.Rendering.Volume in the opened scenes. " +
            "Filter by sceneGlob (wildcard against scene asset path) or onlyWithProfile.")]
        public VolumeListResult ListVolumes
        (
            [Description("Filter by scene asset-path glob (e.g. 'Assets/Scenes/*.unity'). Optional.")]
            string? sceneGlob = null,
            [Description("Include only volumes that have a VolumeProfile assigned. Default false.")]
            bool onlyWithProfile = false
        )
        {
            return MainThread.Instance.Run(() =>
            {
                if (!IsSrpCoreAvailable())
                    return new VolumeListResult { Error = Error.SrpCoreNotInstalled() };

                var volumeType = GetVolumeType();
                if (volumeType == null)
                    return new VolumeListResult { Error = Error.SrpCoreNotInstalled() };

                // The non-generic FindObjectsByType(Type, FindObjectsInactive, FindObjectsSortMode)
                // overload is available in Unity 2022.3+, lets us pass the SRP Volume Type at
                // runtime without a compile-time dependency on the SRP package.
                var raw = UnityEngine.Object.FindObjectsByType(
                    volumeType,
                    FindObjectsInactive.Include,
                    FindObjectsSortMode.None);
                var infos = new List<VolumeInfo>(raw.Length);

                foreach (var obj in raw)
                {
                    if (!(obj is Component vol)) continue;
                    if (vol == null || vol.gameObject == null) continue;

                    var go = vol.gameObject;
                    var scene = go.scene;

                    // sceneGlob filter — when supplied, match against scene asset path. Unsaved /
                    // un-named scenes have an empty path and won't match any non-empty pattern.
                    if (!string.IsNullOrEmpty(sceneGlob))
                    {
                        var scenePath = scene.path ?? "";
                        if (!MatchGlob(sceneGlob, scenePath)) continue;
                    }

                    UnityEngine.Object? profile = GetSharedProfile(vol);
                    if (onlyWithProfile && profile == null) continue;

                    TryGetVolumeBool(vol, "isGlobal", out var isGlobal);
                    TryGetVolumeFloat(vol, "priority", out var priority);
                    TryGetVolumeFloat(vol, "weight", out var weight);

                    var profilePath = profile != null
                        ? AssetDatabase.GetAssetPath(profile)
                        : null;
                    if (string.IsNullOrEmpty(profilePath)) profilePath = null;

                    var overrides = System.Array.Empty<string>();
                    if (profile != null)
                    {
                        var components = GetProfileComponents(profile);
                        if (components != null)
                        {
                            overrides = components
                                .Where(c => c != null)
                                .Select(c => c.GetType().Name)
                                .ToArray();
                        }
                    }

                    infos.Add(new VolumeInfo
                    {
                        GameObjectPath = GetHierarchyPath(go),
                        IsGlobal = isGlobal,
                        Priority = priority,
                        Weight = weight,
                        ProfileAssetPath = profilePath,
                        Overrides = overrides
                    });
                }

                infos.Sort((a, b) => string.CompareOrdinal(a.GameObjectPath, b.GameObjectPath));

                return new VolumeListResult
                {
                    Volumes = infos.ToArray()
                };
            });
        }

        // ---------------------------------------------------------------
        // DTOs scoped to graphics-volume-list
        // ---------------------------------------------------------------

        public class VolumeListResult
        {
            [Description("Discovered Volume components, sorted by hierarchy path.")]
            public VolumeInfo[] Volumes { get; set; } = System.Array.Empty<VolumeInfo>();

            [Description("Error message — populated when SRP Core is missing or the call otherwise " +
                "could not enumerate Volumes. Null on success.")]
            public string? Error { get; set; }
        }

        public class VolumeInfo
        {
            [Description("Slash-separated hierarchy path of the Volume's GameObject.")]
            public string GameObjectPath { get; set; } = "";

            [Description("Volume.isGlobal — global volumes affect the camera at any position.")]
            public bool IsGlobal { get; set; }

            [Description("Volume.priority — higher values win the blend.")]
            public float Priority { get; set; }

            [Description("Volume.weight — blend weight in [0..1].")]
            public float Weight { get; set; }

            [Description("Asset path of the assigned VolumeProfile, or null when none is assigned " +
                "(or the profile is an in-memory instance not yet saved).")]
            public string? ProfileAssetPath { get; set; }

            [Description("Short class names of every VolumeComponent the profile currently holds.")]
            public string[] Overrides { get; set; } = System.Array.Empty<string>();
        }
    }
}
