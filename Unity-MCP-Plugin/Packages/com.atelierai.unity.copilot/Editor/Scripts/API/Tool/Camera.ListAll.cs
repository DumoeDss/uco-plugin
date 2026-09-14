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
using System.Linq;
using com.AtelierAI.Uco.Framework;
using com.IvanMurzak.ReflectorNet.Utils;
using UnityEngine;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    public partial class Tool_Camera
    {
        public const string CameraListAllToolId = "camera-list-all";

        [UcoTool
        (
            CameraListAllToolId,
            Title = "Camera / List All",
            ReadOnlyHint = true,
            IdempotentHint = true
        )]
        [UcoSkillDescription("List every `UnityEngine.Camera` in the active scenes (including inactive ones), " +
            "with a configuration snapshot per camera. Reports `cinemachineInstalled` so callers know whether the " +
            "Cinemachine-specific tools are available.")]
        [UcoSkillBody("Enumerates all Cameras in the loaded scenes via `Object.FindObjectsByType<Camera>(IncludeInactive)` " +
            "and returns a snapshot per camera plus the global Cinemachine-availability flag.\n\n" +
            "## Output shape\n\n" +
            "- `CinemachineInstalled` — true when the Cinemachine package is detected (via reflection).\n" +
            "- `Cameras` — array of `CameraDataSnapshot`. When a camera GameObject also hosts a Cinemachine " +
            "virtual-camera component, `CmType` and `CmPriority` are populated.")]
        [Description("List all Cameras in the scenes with a config snapshot per camera. " +
            "Also reports whether the Cinemachine package is installed.")]
        public CameraListResult ListAll(string? nothing = null)
        {
            return MainThread.Instance.Run(() =>
            {
                var cams = UnityEngine.Object.FindObjectsByType<UnityEngine.Camera>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None);

                var snapshots = cams
                    .Where(c => c != null && c.gameObject != null)
                    .Select(c => BuildSnapshot(c.gameObject))
                    .Where(s => s != null)
                    .Cast<CameraDataSnapshot>()
                    .OrderBy(s => s.Path, System.StringComparer.Ordinal)
                    .ToArray();

                return new CameraListResult
                {
                    CinemachineInstalled = IsCinemachineAvailable(),
                    Cameras = snapshots
                };
            });
        }

        /// <summary>
        /// Result envelope for <see cref="ListAll"/>.
        /// </summary>
        public class CameraListResult
        {
            [Description("True when the Cinemachine package is detected via reflection.")]
            public bool CinemachineInstalled { get; set; }

            [Description("Snapshots for every Camera found in the active scenes, ordered by hierarchy path.")]
            public CameraDataSnapshot[] Cameras { get; set; } = System.Array.Empty<CameraDataSnapshot>();
        }
    }
}
