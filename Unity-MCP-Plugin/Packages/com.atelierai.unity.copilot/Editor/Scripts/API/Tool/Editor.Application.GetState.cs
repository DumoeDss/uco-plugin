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
using AIGD;
using System;
using System.ComponentModel;
using System.Diagnostics;
using com.AtelierAI.Uco.Framework;
using com.IvanMurzak.ReflectorNet.Utils;
using UnityEditor;

namespace AIGD
{
    [Description("Result of a safe, preflighted request to close the Unity Editor normally.")]
    public sealed class EditorCloseRequestResult
    {
        public bool Ok { get; set; }
        public bool Accepted { get; set; }
        public int EditorPid { get; set; }
        public string[] Blockers { get; set; } = Array.Empty<string>();
        public EditorStatsData? State { get; set; }
        public string? Error { get; set; }
    }
}

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    public partial class Tool_Editor
    {
        public const string EditorApplicationGetStateToolId = "editor-application-get-state";
        [UcoTool
        (
            EditorApplicationGetStateToolId,
            Title = "Editor / Application / Get State",
            ReadOnlyHint = true,
            IdempotentHint = true,
            Enabled = false
        )]
        [UcoSkillDescription("Return the current state of `UnityEditor.EditorApplication` — playmode, " +
            "paused state, compilation state, and related flags.")]
        [UcoSkillBody("Returns available information about 'UnityEditor.EditorApplication'. " +
            "Use it to get information about the current state of the Unity Editor application. " +
            "Such as: playmode, paused state, compilation state, etc.\n\n" +
            "## Behavior\n\n" +
            "Snapshots Editor state via `EditorStatsData.FromEditor()` on the main thread and returns the result.")]
        [Description("Returns available information about 'UnityEditor.EditorApplication'. " +
            "Use it to get information about the current state of the Unity Editor application. " +
            "Such as: playmode, paused state, compilation state, etc.")]
        public EditorStatsData? GetApplicationState(string? nothing = null)
        {
            return MainThread.Instance.Run(() =>
            {
                return EditorStatsData.FromEditor();
            });
        }

        [UcoTool(
            "editor-application-request-close",
            Title = "Editor / Application / Request Close",
            DestructiveHint = true,
            IdempotentHint = true)]
        [Description("Preflight unsaved/busy Editor state and schedule a normal close only when the Editor is saved and idle.")]
        public EditorCloseRequestResult RequestApplicationClose(string? nothing = null)
        {
            return MainThread.Instance.Run(() =>
            {
                var state = EditorStatsData.FromEditor();
                var blockers = state.Blockers ?? Array.Empty<string>();
                if (blockers.Length > 0)
                {
                    return new EditorCloseRequestResult
                    {
                        Ok = false,
                        Accepted = false,
                        EditorPid = Process.GetCurrentProcess().Id,
                        Blockers = blockers,
                        State = state,
                        Error = "Editor close refused because the Editor is not saved and idle."
                    };
                }

                var pid = Process.GetCurrentProcess().Id;
                EditorApplication.Exit(0);
                return new EditorCloseRequestResult
                {
                    Ok = true,
                    Accepted = true,
                    EditorPid = pid,
                    State = state
                };
            });
        }
    }
}
