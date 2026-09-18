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
using com.AtelierAI.Unity.Copilot.Editor.Utils;
using UnityEditor;
using UnityEngine;

namespace com.AtelierAI.Unity.Copilot.Editor.UI
{
    /// <summary>
    /// Exposes the team-shared update settings under
    /// <c>Edit ▸ Project Settings ▸ Unity Copilot</c>.
    /// </summary>
    /// <remarks>
    /// Mutations write through to <see cref="UnityCopilotUpdateProjectSettings"/>, whose backing
    /// asset (<c>ProjectSettings/Copilot-UpdateSettings.asset</c>) is intended to be
    /// committed to VCS. See https://github.com/IvanMurzak/uco-plugin/issues/768.
    /// </remarks>
    internal static class UnityCopilotProjectSettingsProvider
    {
        private const string SettingsPath = "Project/Unity Copilot";

        [SettingsProvider]
        public static SettingsProvider Create() => new SettingsProvider(SettingsPath, SettingsScope.Project)
        {
            label = "Unity Copilot",
            guiHandler = _ =>
            {
                EditorGUILayout.LabelField("Update Notifications", EditorStyles.boldLabel);

                EditorGUI.BeginChangeCheck();
                var newValue = EditorGUILayout.ToggleLeft(
                    new GUIContent(
                        "Disable update notifications for the entire team",
                        "Stored in ProjectSettings/ and shared with everyone who clones this project. " +
                        "When enabled, the update popup is suppressed for every team member, regardless " +
                        "of their per-user 'Do not show again' state."),
                    UpdateChecker.IsDisabledForProject);
                if (EditorGUI.EndChangeCheck())
                    UpdateChecker.IsDisabledForProject = newValue;

                EditorGUILayout.Space();
                EditorGUILayout.HelpBox(
                    "This setting is stored in ProjectSettings/Copilot-UpdateSettings.asset " +
                    "and is shared with everyone who clones this project. The per-user 'Do not show again' " +
                    "button on the update popup is unaffected by this setting.",
                    MessageType.Info);
            },
            keywords = new HashSet<string>
            {
                "AI", "Copilot", "Unity Copilot", "OpenUPM", "Update", "Notifications", "Team", "Game", "Developer"
            }
        };
    }
}
