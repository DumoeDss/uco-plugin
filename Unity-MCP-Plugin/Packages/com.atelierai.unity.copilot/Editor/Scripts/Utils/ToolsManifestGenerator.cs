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
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using com.AtelierAI.Unity.Copilot.Editor.Branding;
using com.IvanMurzak.McpPlugin;
using UnityEditor;
using UnityEngine;

namespace com.AtelierAI.Unity.Copilot.Editor.Utils
{
    /// <summary>
    /// Generates <c>tools-manifest.json</c> — a static snapshot of the plugin's
    /// tool catalog in the SAME shape as the server's <c>GET /api/tools</c>
    /// response. The manifest ships inside the package so that <c>cocli init</c>
    /// can generate eager skill reference docs offline (no running Unity Editor
    /// or MCP server required).
    /// <para>
    /// Each entry contains <c>name</c>, <c>enabled</c>, <c>title</c>,
    /// <c>description</c>, <c>inputSchema</c>, and optionally <c>outputSchema</c>
    /// — exactly matching <see cref="com.IvanMurzak.McpPlugin.Server.Api.DirectToolCallEndpoints"/>.
    /// The <c>enabled</c> field reflects the DEFAULT state from
    /// <see cref="McpPluginToolAttribute"/> (not per-project runtime overrides)
    /// so the shipped manifest is deterministic across projects.
    /// </para>
    /// <para>
    /// Menu: <b>Tools / AI Game Developer / Generate Tools Manifest</b>.
    /// Requires an embedded or local-copy package installation (writes into
    /// <c>Packages/com.atelierai.unity.copilot/</c>).
    /// </para>
    /// </summary>
    public static class ToolsManifestGenerator
    {
        public const string ManifestFileName = "tools-manifest.json";

        [MenuItem(ProductInfo.ToolsMenuRoot + "/Generate Tools Manifest", priority = 2100)]
        public static void GenerateManifest()
        {
            // Ensure the plugin is built — it normally is via [InitializeOnLoad].
            var plugin = UnityCopilotPluginEditor.CurrentPlugin;
            if (plugin == null)
            {
                UnityCopilotPluginEditor.Instance.BuildMcpPluginIfNeeded();
                plugin = UnityCopilotPluginEditor.CurrentPlugin;
            }
            if (plugin == null)
            {
                Debug.LogError(
                    $"{ProductInfo.LogPrefix} Failed to build MCP plugin; cannot generate tools manifest.");
                return;
            }

            var toolManager = plugin.McpManager?.ToolManager;
            if (toolManager == null)
            {
                Debug.LogError(
                    $"{ProductInfo.LogPrefix} ToolManager is null; cannot generate tools manifest.");
                return;
            }

            // Build a JSON array whose entries match the /api/tools response shape
            // (see DirectToolCallEndpoints.ListToolsHandler).
            var tools = toolManager.GetAllTools()
                .OrderBy(t => t.Name, StringComparer.Ordinal)
                .ToList();

            var toolsArray = new JsonArray();
            foreach (var tool in tools)
            {
                var entry = new JsonObject
                {
                    ["name"] = tool.Name,
                    ["enabled"] = ResolveDefaultEnabled(tool),
                };
                if (tool.Title != null)
                    entry["title"] = tool.Title;
                if (tool.Description != null)
                    entry["description"] = tool.Description;
                if (tool.InputSchema != null)
                    entry["inputSchema"] = JsonNode.Parse(tool.InputSchema.ToJsonString());
                if (tool.OutputSchema != null)
                    entry["outputSchema"] = JsonNode.Parse(tool.OutputSchema.ToJsonString());

                toolsArray.Add(entry);
            }

            var packageRoot = ResolvePackageRoot();
            if (!Directory.Exists(packageRoot))
            {
                Debug.LogError(
                    $"{ProductInfo.LogPrefix} Package directory not found: {packageRoot}\n" +
                    "The plugin must be installed as an embedded or local-copy package.");
                return;
            }

            var manifestPath = Path.Combine(packageRoot, ManifestFileName);
            var jsonOptions = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(manifestPath, toolsArray.ToJsonString(jsonOptions) + "\n");

            AssetDatabase.Refresh();
            Debug.Log(
                $"{ProductInfo.LogPrefix} Tools manifest generated: {manifestPath} ({toolsArray.Count} tools)");
        }

        [MenuItem(ProductInfo.ToolsMenuRoot + "/Generate Tools Manifest", validate = true)]
        public static bool ValidateGenerateManifest()
        {
            // Only show the menu when the editor singleton is available.
            return UnityCopilotPluginEditor.HasInstance;
        }

        /// <summary>
        /// Resolves the DEFAULT enabled state from the <c>[McpPluginTool]</c>
        /// attribute — NOT the runtime-overridden state. This ensures the
        /// shipped manifest reflects plugin defaults rather than per-project
        /// config overrides applied by <c>ApplyConfigToMcpPlugin</c>.
        /// </summary>
        static bool ResolveDefaultEnabled(IRunTool tool)
        {
            // IRunTool.Method exposes the underlying MethodInfo.
            var attr = tool.Method?.GetCustomAttribute<McpPluginToolAttribute>();
            // EnabledValue returns null when not explicitly set → default is true.
            return attr?.EnabledValue ?? true;
        }

        /// <summary>
        /// Resolves the on-disk root directory of this package. Checks for
        /// an embedded/local-copy package first (<c>Packages/&lt;id&gt;</c>),
        /// then falls back to the Library/PackageCache.
        /// </summary>
        static string ResolvePackageRoot()
        {
            var projectRoot = Path.GetDirectoryName(Application.dataPath);
            if (string.IsNullOrEmpty(projectRoot))
            {
                Debug.LogError($"{ProductInfo.LogPrefix} Could not determine project root.");
                return ManifestFileName;
            }

            var embeddedPath = Path.Combine(projectRoot, "Packages", ProductInfo.PackageId);
            if (Directory.Exists(embeddedPath))
                return embeddedPath;

            // Package cache layout: Library/PackageCache/<id>@<version>/
            var cachePath = Path.Combine(projectRoot, "Library", "PackageCache");
            if (Directory.Exists(cachePath))
            {
                var matches = Directory.GetDirectories(cachePath, ProductInfo.PackageId + "*");
                if (matches.Length > 0)
                    return matches[0];
            }

            // Default: return the embedded path even if it doesn't exist yet
            // (the caller will error with a clear message).
            return embeddedPath;
        }
    }
}
#endif
