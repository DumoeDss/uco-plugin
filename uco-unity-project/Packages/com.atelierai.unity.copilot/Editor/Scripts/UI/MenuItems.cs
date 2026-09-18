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
#if UNITY_EDITOR
using com.AtelierAI.Unity.Copilot.Editor.Branding;
using com.AtelierAI.Unity.Copilot.Editor.Utils;
using UnityEditor;
using UnityEngine;

namespace com.AtelierAI.Unity.Copilot.Editor.UI
{
    public static class MenuItems
    {
        [MenuItem(ProductInfo.MainWindowMenu, priority = 1006)]
        public static void ShowWindow() => MainWindowEditor.ShowWindow();

        [MenuItem(ProductInfo.ToolsMenuRoot + "/Updates/Check for Updates", priority = 999)]
        public static void CheckForUpdates() => _ = UpdateChecker.CheckForUpdatesAsync(forceCheck: true);

        // Team-shared kill-switch for the update popup. Toggling this writes through to
        // ProjectSettings/Copilot-UpdateSettings.asset (intended to be committed
        // to VCS). The validate method renders the menu's check-mark to match current state.
        // See https://github.com/IvanMurzak/uco-plugin/issues/768.
        private const string DisableUpdatesMenu = ProductInfo.ToolsMenuRoot + "/Updates/Disable Update Notifications (Team)";

        [MenuItem(DisableUpdatesMenu, priority = 1000)]
        public static void ToggleDisableUpdatesForTeam()
            => UpdateChecker.IsDisabledForProject = !UpdateChecker.IsDisabledForProject;

        [MenuItem(DisableUpdatesMenu, validate = true)]
        public static bool ToggleDisableUpdatesForTeamValidate()
        {
            Menu.SetChecked(DisableUpdatesMenu, UpdateChecker.IsDisabledForProject);
            return true;
        }

        // NOTE: The old .NET server binary flow (Download Binaries / Delete Binaries /
        // Open Logs / Open Log Errors) was removed together with the staged
        // Library/server binary (legacy .NET era). The local server is the Node.js bridge
        // (cocli) launched directly by CopilotServerManager; its stdout/stderr is
        // surfaced in the Editor console.

        [MenuItem(ProductInfo.ToolsMenuRoot + "/Debug/Show Update Popup", priority = 2000)]
        public static void ShowUpdatePopup() => UpdatePopupWindow.ShowWindow(UnityCopilotPlugin.Version, "99.99.99");

        [MenuItem(ProductInfo.ToolsMenuRoot + "/Debug/Reset Update Preferences", priority = 2001)]
        public static void ResetUpdatePreferences()
        {
            UpdateChecker.ClearPreferences();
            Debug.Log("Update preferences have been reset.");
        }

        [MenuItem(ProductInfo.ToolsMenuRoot + "/Debug/Serialization Check", priority = 2002)]
        public static void ShowSerializationCheck() => SerializationCheckWindow.ShowWindow();

        [MenuItem(ProductInfo.ToolsMenuRoot + "/Reset Config", priority = 2020)]
        public static void ResetConfig()
        {
            UnityCopilotPluginEditor.ResetConfig();
            // Reload Domain to ensure all changes are picked up.
            EditorUtility.RequestScriptReload();
        }
    }
}
#endif