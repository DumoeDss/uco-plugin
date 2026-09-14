/*
┌──────────────────────────────────────────────────────────────────┐
│  Adapted from MCP for Unity (https://github.com/CoplayDev/unity-mcp)
│  Copyright (c) 2025 Coplay Inc.                                  │
│  Licensed under the MIT License.                                 │
│  See the LICENSE file in the project root for more information.  │
└──────────────────────────────────────────────────────────────────┘
┌──────────────────────────────────────────────────────────────────┐
│  Author: Ivan Murzak (https://github.com/IvanMurzak)             │
│  Repository: GitHub (https://github.com/IvanMurzak/Unity-MCP)    │
│  Copyright (c) 2025 Ivan Murzak                                  │
│  Licensed under the Apache License, Version 2.0.                 │
│  See the LICENSE file in the project root for more information.  │
└──────────────────────────────────────────────────────────────────┘
*/

#nullable enable
using AIGD;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using com.AtelierAI.Uco.Framework;
using com.IvanMurzak.ReflectorNet.Utils;
using UProfiler = UnityEngine.Profiling.Profiler;
using ProfilerArea = UnityEngine.Profiling.ProfilerArea;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    public partial class Tool_Profiler
    {
        private static readonly string[] s_ProfilerAreaNames = Enum.GetNames(typeof(ProfilerArea));

        public const string ProfilerStartToolId = "profiler-start";
        [UcoTool
        (
            ProfilerStartToolId,
            Title = "Profiler / Start",
            DestructiveHint = true,
            Enabled = false
        )]
        [UcoSkillDescription("Enable the Unity Profiler. Optionally record to a binary `.raw` log file and " +
            "enable allocation callstacks. Pair with '" + ProfilerStopToolId + "' to end the session and '" +
            ProfilerStatusToolId + "' to inspect state.")]
        [UcoSkillBody("Enable the Unity Profiler. " +
            "Optionally record the session to a binary `.raw` log file via `Profiler.logFile` and enable " +
            "allocation callstacks for managed allocations.\n\n" +
            "## Inputs\n\n" +
            "- `logFile` (optional) — absolute path for the `.raw` recording. Parent directory must already exist.\n" +
            "- `enableCallstacks` (optional, default `false`) — toggle `Profiler.enableAllocationCallstacks`.\n\n" +
            "## Behavior\n\n" +
            "Sets `Profiler.enabled = true`. When `logFile` is supplied also sets `Profiler.logFile` and " +
            "`Profiler.enableBinaryLog = true`. Always runs on the Unity main thread.")]
        [Description("Enable the Unity Profiler. Optionally record to a .raw file and enable allocation callstacks.")]
        public ProfilerStatusResult Start
        (
            [Description("Absolute path for an optional binary '.raw' profiler recording. The parent directory must exist.")]
            string? logFile = null,
            [Description("If true, enables Profiler.enableAllocationCallstacks. Default: false.")]
            bool? enableCallstacks = null
        )
        {
            return MainThread.Instance.Run(() =>
            {
                UProfiler.enabled = true;

                if (!string.IsNullOrEmpty(logFile))
                {
                    var dir = Path.GetDirectoryName(logFile);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        throw new InvalidOperationException(Error.LogDirectoryNotFound(dir!));

                    UProfiler.logFile = logFile!;
                    UProfiler.enableBinaryLog = true;
                }

                if (enableCallstacks.HasValue)
                    UProfiler.enableAllocationCallstacks = enableCallstacks.Value;

                return BuildStatus();
            });
        }

        public const string ProfilerStopToolId = "profiler-stop";
        [UcoTool
        (
            ProfilerStopToolId,
            Title = "Profiler / Stop",
            DestructiveHint = true,
            IdempotentHint = true,
            Enabled = false
        )]
        [UcoSkillDescription("Disable the Unity Profiler and stop any active `.raw` recording started by '" +
            ProfilerStartToolId + "'.")]
        [UcoSkillBody("Disable the Unity Profiler. " +
            "Stops any active `.raw` recording, resets `Profiler.enableBinaryLog = false`, clears " +
            "`Profiler.logFile`, and disables allocation callstacks. Idempotent — safe to call when already stopped.")]
        [Description("Disable the Unity Profiler and stop recording.")]
        public ProfilerStatusResult Stop(string? nothing = null)
        {
            return MainThread.Instance.Run(() =>
            {
                var previousLog = UProfiler.enableBinaryLog ? UProfiler.logFile : null;

                UProfiler.enableBinaryLog = false;
                UProfiler.logFile = string.Empty;
                UProfiler.enableAllocationCallstacks = false;
                UProfiler.enabled = false;

                var status = BuildStatus();
                status.PreviousLogFile = previousLog;
                return status;
            });
        }

        public const string ProfilerStatusToolId = "profiler-status";
        [UcoTool
        (
            ProfilerStatusToolId,
            Title = "Profiler / Status",
            ReadOnlyHint = true,
            IdempotentHint = true,
            Enabled = false
        )]
        [UcoSkillDescription("Return the current state of the Unity Profiler — enabled flag, binary log " +
            "recording info, allocation callstacks flag, and per-`ProfilerArea` enable map.")]
        [UcoSkillBody("Return a snapshot of the Unity Profiler state: " +
            "`Profiler.enabled`, `Profiler.enableBinaryLog`, `Profiler.logFile`, " +
            "`Profiler.enableAllocationCallstacks`, and every `ProfilerArea`'s enabled flag.\n\n" +
            "Read-only and idempotent — safe to poll.")]
        [Description("Return Unity Profiler state and the per-area enabled map.")]
        public ProfilerStatusResult Status(string? nothing = null)
        {
            return MainThread.Instance.Run(() => BuildStatus());
        }

        public const string ProfilerSetAreasToolId = "profiler-set-areas";
        [UcoTool
        (
            ProfilerSetAreasToolId,
            Title = "Profiler / Set Areas",
            DestructiveHint = true,
            IdempotentHint = true,
            Enabled = false
        )]
        [UcoSkillDescription("Enable or disable specific Unity `ProfilerArea` values in batch. " +
            "Use '" + ProfilerStatusToolId + "' to see the current per-area map.")]
        [UcoSkillBody("Toggle one or more `ProfilerArea` values via `Profiler.SetAreaEnabled`. " +
            "Unknown area names throw with the full list of valid names.\n\n" +
            "## Inputs\n\n" +
            "- `areas` — dictionary of `ProfilerArea` name → enabled flag (case-insensitive match).\n\n" +
            "## Behavior\n\n" +
            "Applies each entry, then returns the post-update status snapshot.")]
        [Description("Toggle Unity ProfilerArea values via Profiler.SetAreaEnabled.")]
        public ProfilerStatusResult SetAreas
        (
            [Description("Dictionary of ProfilerArea name -> bool. Use 'profiler-status' for valid area names.")]
            Dictionary<string, bool> areas
        )
        {
            if (areas == null || areas.Count == 0)
                throw new ArgumentException(Error.ParameterRequired(nameof(areas)));

            return MainThread.Instance.Run(() =>
            {
                foreach (var kv in areas)
                {
                    if (!Enum.TryParse<ProfilerArea>(kv.Key, ignoreCase: true, out var area))
                        throw new ArgumentException(Error.UnknownProfilerArea(kv.Key, string.Join(", ", s_ProfilerAreaNames)));

                    UProfiler.SetAreaEnabled(area, kv.Value);
                }
                return BuildStatus();
            });
        }

        private static ProfilerStatusResult BuildStatus()
        {
            var areas = new Dictionary<string, bool>(s_ProfilerAreaNames.Length);
            foreach (var name in s_ProfilerAreaNames)
            {
                if (Enum.TryParse<ProfilerArea>(name, out var area))
                    areas[name] = UProfiler.GetAreaEnabled(area);
            }

            return new ProfilerStatusResult
            {
                Enabled = UProfiler.enabled,
                Recording = UProfiler.enableBinaryLog,
                LogFile = UProfiler.enableBinaryLog ? UProfiler.logFile : null,
                AllocationCallstacks = UProfiler.enableAllocationCallstacks,
                Areas = areas
            };
        }
    }
}

namespace AIGD
{
    [System.ComponentModel.Description("Unity Profiler session status snapshot.")]
    public class ProfilerStatusResult
    {
        [System.ComponentModel.Description("Whether 'UnityEngine.Profiling.Profiler.enabled' is true.")]
        public bool Enabled { get; set; }

        [System.ComponentModel.Description("Whether the profiler is currently recording to a binary log.")]
        public bool Recording { get; set; }

        [System.ComponentModel.Description("Current 'Profiler.logFile' path when 'Recording' is true; otherwise null.")]
        public string? LogFile { get; set; }

        [System.ComponentModel.Description("Whether 'Profiler.enableAllocationCallstacks' is true.")]
        public bool AllocationCallstacks { get; set; }

        [System.ComponentModel.Description("Per-ProfilerArea enabled flag map.")]
        public System.Collections.Generic.Dictionary<string, bool> Areas { get; set; }
            = new System.Collections.Generic.Dictionary<string, bool>();

        [System.ComponentModel.Description("On 'Stop', the log file path captured before recording stopped.")]
        public string? PreviousLogFile { get; set; }
    }
}
