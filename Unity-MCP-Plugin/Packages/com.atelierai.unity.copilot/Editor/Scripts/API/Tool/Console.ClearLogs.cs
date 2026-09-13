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
using UnityEngine;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    public sealed class ConsoleClearTargetResult
    {
        public bool Ok { get; set; }
        public string Strategy { get; set; } = string.Empty;
        public string? Path { get; set; }
        public string? RetainedPath { get; set; }
        public string? Error { get; set; }
    }

    public sealed class ConsoleClearLogsResult
    {
        public bool Ok { get; set; }
        public ConsoleClearTargetResult UnityConsole { get; set; } = new();
        public ConsoleClearTargetResult PersistedLog { get; set; } = new();
    }

    public partial class Tool_Console
    {
        public const string ConsoleClearLogsToolId = "console-clear-logs";
        [McpPluginTool
        (
            ConsoleClearLogsToolId,
            Title = "Console / Clear Logs",
            Enabled = false,
            DestructiveHint = true,
            IdempotentHint = true
        )]
        [McpPluginSkillDescription("Clear the log cache (used by '" + ConsoleGetLogsToolId + "') and the Unity Editor Console window. " +
            "Useful for isolating logs to a specific action by clearing the slate first.")]
        [McpPluginSkillBody("Clears the log cache (used by console-get-logs) and the Unity Editor Console window. " +
            "Useful for isolating errors related to a specific action by clearing logs before performing the action.\n\n" +
            "## Behavior\n\n" +
            "Calls `Debug.ClearDeveloperConsole()` to wipe the Editor Console, then clears the plugin-side `LogCollector` " +
            "cache so subsequent '" + ConsoleGetLogsToolId + "' calls only see new entries.")]
        [Description("Clears the log cache (used by console-get-logs) and the Unity Editor Console window. " +
            "Useful for isolating errors related to a specific action by clearing logs before performing the action.")]
        public ConsoleClearLogsResult ClearLogs(string? nothing = null)
        {
            return MainThread.Instance.Run(() =>
            {
                var result = new ConsoleClearLogsResult();
                try
                {
                    Debug.ClearDeveloperConsole();
                    result.UnityConsole = new ConsoleClearTargetResult
                    {
                        Ok = true,
                        Strategy = "clear-developer-console"
                    };
                }
                catch (Exception ex)
                {
                    result.UnityConsole = new ConsoleClearTargetResult
                    {
                        Ok = false,
                        Strategy = "failed",
                        Error = ex.GetBaseException().Message
                    };
                }

                if (!UnityCopilotPluginEditor.HasInstance)
                {
                    result.PersistedLog = new ConsoleClearTargetResult
                    {
                        Ok = false,
                        Strategy = "plugin-unavailable",
                        Error = "UnityCopilotPluginEditor is not initialized."
                    };
                    result.Ok = false;
                    return result;
                }

                var logCollector = UnityCopilotPluginEditor.Instance.LogCollector;
                if (logCollector == null)
                {
                    result.PersistedLog = new ConsoleClearTargetResult
                    {
                        Ok = false,
                        Strategy = "collector-unavailable",
                        Error = "LogCollector is not initialized."
                    };
                    result.Ok = false;
                    return result;
                }

                try
                {
                    var persisted = logCollector.Clear();
                    result.PersistedLog = new ConsoleClearTargetResult
                    {
                        Ok = persisted.Ok,
                        Strategy = persisted.Strategy,
                        Path = persisted.Path,
                        RetainedPath = persisted.RetainedPath,
                        Error = persisted.Error
                    };
                }
                catch (Exception ex)
                {
                    result.PersistedLog = new ConsoleClearTargetResult
                    {
                        Ok = false,
                        Strategy = "failed",
                        Error = ex.GetBaseException().Message
                    };
                }

                result.Ok = result.UnityConsole.Ok && result.PersistedLog.Ok;
                return result;
            });
        }
    }
}
