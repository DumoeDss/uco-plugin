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
using com.IvanMurzak.McpPlugin;
using com.IvanMurzak.ReflectorNet.Utils;
using com.AtelierAI.Unity.Copilot.Runtime.Utils;
using UnityEditor;
using UnityEngine;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    public partial class Tool_Build
    {
        public const string BuildSwitchPlatformToolId = "build-switch-platform";

        [McpPluginTool
        (
            BuildSwitchPlatformToolId,
            Title = "Build / Switch Platform",
            DestructiveHint = true
        )]
        [McpPluginSkillDescription("Switch the active editor build target via " +
            "`EditorUserBuildSettings.SwitchActiveBuildTarget`. This call is synchronous and may block " +
            "the editor for several minutes while assets are re-imported for the new platform — be " +
            "deliberate about when this is invoked.")]
        [McpPluginSkillBody("Switch the editor's active build target. " +
            "Unity will re-import every asset that depends on platform-specific settings, which can take " +
            "minutes on large projects. The call blocks until Unity returns control, so polling is not " +
            "required.\n\n" +
            "## Inputs\n\n" +
            "- `buildTarget` — one of `StandaloneWindows64`, `StandaloneOSX`, `StandaloneLinux64`, " +
            "`Android`, `iOS`, `WebGL`, plus the usual aliases.\n\n" +
            "## Returns\n\n" +
            "- `PlatformSwitchResult` with previous/current build target and total duration in seconds.")]
        [Description("Switch the editor's active build target. Blocks for the duration of the reimport.")]
        public PlatformSwitchResult SwitchPlatform
        (
            [Description("Target platform identifier (e.g. 'StandaloneWindows64', 'Android').")]
            string buildTarget
        )
        {
            return MainThread.Instance.Run(() =>
            {
                if (!BuildTargetMap.TryResolve(buildTarget, out var target))
                    throw new ArgumentException(Error.InvalidBuildTarget(buildTarget,
                        string.Join(", ", BuildTargetMap.ValidNames)), nameof(buildTarget));

                var previous = EditorUserBuildSettings.activeBuildTarget;
                var group = BuildTargetMap.GetTargetGroup(target);

                if (previous == target)
                {
                    return new PlatformSwitchResult
                    {
                        Switched = true,
                        PreviousBuildTarget = previous.ToString(),
                        ActiveBuildTarget = previous.ToString(),
                        DurationSeconds = 0.0,
                    };
                }

                if (UnityCopilotPlugin.IsLogEnabled(LogLevel.Warning))
                {
                    Debug.LogWarning($"[Build] Switching active build target from {previous} to {target}. " +
                                     "This is synchronous and may block the editor for several minutes " +
                                     "while assets are re-imported.");
                }

                var started = DateTime.UtcNow;
                var ok = EditorUserBuildSettings.SwitchActiveBuildTarget(group, target);
                var duration = (DateTime.UtcNow - started).TotalSeconds;

                var current = EditorUserBuildSettings.activeBuildTarget;
                return new PlatformSwitchResult
                {
                    Switched = ok && current == target,
                    PreviousBuildTarget = previous.ToString(),
                    ActiveBuildTarget = current.ToString(),
                    DurationSeconds = duration,
                };
            });
        }
    }
}
