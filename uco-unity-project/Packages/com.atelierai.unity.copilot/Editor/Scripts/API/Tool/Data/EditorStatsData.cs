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
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json.Serialization;
using com.AtelierAI.Uco.Framework;
using com.IvanMurzak.ReflectorNet.Model;
using com.AtelierAI.Unity.Copilot.Editor.Utils;
using AIGD;
using UnityEditor;
using UnityEditor.SceneManagement;

namespace AIGD
{
    [Description("Available information about 'UnityEditor.EditorApplication'.")]
    public class EditorStatsData
    {
        [Description("Whether the Editor is in Play mode.")]
        public bool IsPlaying { get; set; } = false;

        [Description("Whether the Editor is paused.")]
        public bool IsPaused { get; set; } = false;

        [Description("Is editor currently compiling scripts? (Read Only)")]
        public bool IsCompiling { get; set; } = false;

        [Description("Editor application state which is true only when the Editor is currently in or about to enter Play mode. (Read Only)")]
        public bool IsPlayingOrWillChangePlaymode { get; set; } = false;

        [Description("True if the Editor is currently refreshing the AssetDatabase. (Read Only)")]
        public bool IsUpdating { get; set; } = false;

        [Description("Path to the Unity editor contents folder. (Read Only)")]
        public string ApplicationContentsPath { get; set; } = string.Empty;

        [Description("Gets the path to the Unity Editor application. (Read Only)")]
        public string ApplicationPath { get; set; } = string.Empty;

        [Description("The time since the editor was started. (Read Only)")]
        public double TimeSinceStartup { get; set; } = 0;

        [Description("True when one or more open scenes have unsaved changes.")]
        public bool HasUnsavedScenes { get; set; }

        [Description("Paths or names of open scenes with unsaved changes.")]
        public string[] DirtyScenes { get; set; } = Array.Empty<string>();

        [Description("Bounded close/readiness blockers derived from the current Editor state.")]
        public string[] Blockers { get; set; } = Array.Empty<string>();

        [Description("Canonical staged Editor readiness and scheduler snapshot.")]
        public EditorReadinessSnapshot Readiness { get; set; } = new();

        public static EditorStatsData FromEditor()
        {
            var dirtyScenes = new List<string>();
            for (var i = 0; i < EditorSceneManager.sceneCount; i++)
            {
                var scene = EditorSceneManager.GetSceneAt(i);
                if (!scene.isDirty) continue;
                dirtyScenes.Add(string.IsNullOrWhiteSpace(scene.path) ? scene.name : scene.path);
            }

            var readiness = EditorToolExecutionScheduler.Shared.Snapshot(
                com.AtelierAI.Unity.Copilot.UnityCopilotPluginEditor.HasInstance
                && com.AtelierAI.Unity.Copilot.UnityCopilotPluginEditor.ConnectionState.CurrentValue
                    == com.AtelierAI.Uco.Framework.ConnectionState.Connected,
                ignoreCurrentSerializedCall: true);
            var blockers = new List<string>();
            if (EditorApplication.isCompiling) blockers.Add("compiling");
            if (EditorApplication.isUpdating) blockers.Add("updating-or-importing");
            if (EditorApplication.isPlaying) blockers.Add("play-mode");
            else if (EditorApplication.isPlayingOrWillChangePlaymode) blockers.Add("play-mode-transition");
            if (dirtyScenes.Count > 0) blockers.Add("unsaved-scenes");

            return new EditorStatsData
            {
                IsPlaying = EditorApplication.isPlaying,
                IsPaused = EditorApplication.isPaused,
                IsCompiling = EditorApplication.isCompiling,
                IsPlayingOrWillChangePlaymode = EditorApplication.isPlayingOrWillChangePlaymode,
                IsUpdating = EditorApplication.isUpdating,
                ApplicationContentsPath = EditorApplication.applicationContentsPath,
                ApplicationPath = EditorApplication.applicationPath,
                TimeSinceStartup = EditorApplication.timeSinceStartup,
                HasUnsavedScenes = dirtyScenes.Count > 0,
                DirtyScenes = dirtyScenes.ToArray(),
                Blockers = blockers.Distinct(StringComparer.Ordinal).Take(8).ToArray(),
                Readiness = readiness
            };
        }
    }
}
