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

        [MenuItem("Tools/AI Game Developer/Updates/Check for Updates", priority = 999)]
        public static void CheckForUpdates() => _ = UpdateChecker.CheckForUpdatesAsync(forceCheck: true);

        // Team-shared kill-switch for the update popup. Toggling this writes through to
        // ProjectSettings/AI-Game-Developer-UpdateSettings.asset (intended to be committed
        // to VCS). The validate method renders the menu's check-mark to match current state.
        // See https://github.com/IvanMurzak/Unity-MCP/issues/768.
        private const string DisableUpdatesMenu = "Tools/AI Game Developer/Updates/Disable Update Notifications (Team)";

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
        // Library/mcp-server binary. The local server is now the Node.js MCP server
        // (cocli) launched directly by CopilotServerManager; its stdout/stderr is
        // surfaced in the Editor console.

        [MenuItem("Tools/AI Game Developer/Server/Launch MCP Inspector", priority = 1004)]
        public static void LaunchMcpInspector()
        {
            // Run command in a terminal window: npx @modelcontextprotocol/inspector http://localhost:8080 --transport http
            var npxArgs = $"-y @modelcontextprotocol/inspector {UnityCopilotPluginEditor.Host} --transport http";
            Debug.Log($"Launching MCP Inspector with command: npx {npxArgs}");

            var processInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "npx",
                Arguments = npxArgs,
                UseShellExecute = true,
                CreateNoWindow = false,
            };

            try
            {
                System.Diagnostics.Process.Start(processInfo);
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                var command = $"{processInfo.FileName} {processInfo.Arguments}";
                NotificationPopupWindow.Show(
                    windowTitle: "Launch Failed",
                    title: "Unable to start MCP Inspector",
                    message:
                        "The MCP Inspector could not be started from Unity.\n\n" +
                        "This usually means that Node.js (and npx) is not installed, or 'npx' is not available on your PATH.\n\n" +
                        "Prerequisites:\n" +
                        " - Install Node.js (which includes npx)\n" +
                        " - Ensure 'npx' is available from your terminal/command prompt\n\n" +
                        "You can try running the following command manually in a terminal:\n" +
                        command + "\n\n" +
                        "System error:\n" +
                        ex.Message,
                    width: 450,
                    minWidth: 450,
                    height: 460,
                    minHeight: 460);
            }
            catch (System.Exception ex)
            {
                var command = $"{processInfo.FileName} {processInfo.Arguments}";
                NotificationPopupWindow.Show(
                    windowTitle: "Launch Failed",
                    title: "Unexpected error starting MCP Inspector",
                    message:
                        "An unexpected error occurred while trying to start the MCP Inspector.\n\n" +
                        "You can try running the following command manually in a terminal:\n" +
                        command + "\n\n" +
                        "Error details:\n" +
                        ex.Message,
                    width: 450,
                    minWidth: 450,
                    height: 460,
                    minHeight: 460);
            }
        }

        [MenuItem("Tools/AI Game Developer/Debug/Show Update Popup", priority = 2000)]
        public static void ShowUpdatePopup() => UpdatePopupWindow.ShowWindow(UnityCopilotPlugin.Version, "99.99.99");

        [MenuItem("Tools/AI Game Developer/Debug/Reset Update Preferences", priority = 2001)]
        public static void ResetUpdatePreferences()
        {
            UpdateChecker.ClearPreferences();
            Debug.Log("Update preferences have been reset.");
        }

        [MenuItem("Tools/AI Game Developer/Debug/Serialization Check", priority = 2002)]
        public static void ShowSerializationCheck() => SerializationCheckWindow.ShowWindow();

        [MenuItem("Tools/AI Game Developer/Reset Config", priority = 2020)]
        public static void ResetConfig()
        {
            UnityCopilotPluginEditor.ResetConfig();
            // Reload Domain to ensure all changes are picked up.
            EditorUtility.RequestScriptReload();
        }
    }
}
#endif