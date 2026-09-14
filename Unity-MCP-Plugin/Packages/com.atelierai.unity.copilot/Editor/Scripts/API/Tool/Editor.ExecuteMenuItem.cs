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
using UnityEditor;

namespace AIGD
{
    [Description("Result of executing a Unity Editor menu item via 'EditorApplication.ExecuteMenuItem'.")]
    public class MenuItemExecuteResult
    {
        [Description("True when the menu item was found, enabled and executed; false otherwise.")]
        public bool Ok { get; set; }

        [Description("The full menu path that was requested, echoed back for traceability.")]
        public string MenuPath { get; set; } = string.Empty;

        [Description("Error message when Ok is false. Null on success.")]
        public string? Error { get; set; }
    }
}

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    using AIGD;

    public partial class Tool_Editor
    {
        public const string EditorExecuteMenuItemToolId = "editor-execute-menu-item";

        [McpPluginTool
        (
            EditorExecuteMenuItemToolId,
            Title = "Editor / Execute Menu Item",
            DestructiveHint = true,
            Enabled = false
        )]
        [McpPluginSkillDescription("Invoke a Unity Editor menu item by its full menu path (e.g. " +
            "'GameObject/Create Empty', 'Assets/Refresh'). Use the 'editor://menu-items' resource to " +
            "discover the canonical list of available menu paths first.")]
        [McpPluginSkillBody("Executes an Editor menu item via `UnityEditor.EditorApplication.ExecuteMenuItem`.\n\n" +
            "## Inputs\n\n" +
            "- `menuPath` — Full menu path string, e.g. `'GameObject/Create Empty'`, `'Assets/Refresh'`. " +
            "Throws `ArgumentException` when null or empty.\n\n" +
            "## Behavior\n\n" +
            "Runs on the Unity main thread. The wrapped Unity API returns `false` when the menu item does " +
            "not exist or is currently disabled (e.g. a validator method rejected it). In that case this " +
            "tool returns `Ok=false` with a populated `Error` message instead of throwing.\n\n" +
            "## Discovery\n\n" +
            "Use the `editor://menu-items` resource to enumerate every `[MenuItem]` registered in the " +
            "project so you can pick the exact canonical path before calling this tool.")]
        [Description("Execute a Unity Editor menu item by full path. " +
            "Use the 'editor://menu-items' resource to list available menu paths first.")]
        public MenuItemExecuteResult ExecuteMenuItem
        (
            [Description("Full menu path, e.g. 'GameObject/Create Empty', 'Assets/Refresh'. " +
                "Get the canonical list from the 'editor://menu-items' resource.")]
            string menuPath
        )
        {
            if (string.IsNullOrWhiteSpace(menuPath))
                throw new ArgumentException("Menu path is null or empty. Provide a full menu path like 'GameObject/Create Empty'.", nameof(menuPath));

            return MainThread.Instance.Run(() =>
            {
                var ok = EditorApplication.ExecuteMenuItem(menuPath);
                return new MenuItemExecuteResult
                {
                    Ok = ok,
                    MenuPath = menuPath,
                    Error = ok ? null : $"Menu item not found or disabled: '{menuPath}'."
                };
            });
        }
    }
}
