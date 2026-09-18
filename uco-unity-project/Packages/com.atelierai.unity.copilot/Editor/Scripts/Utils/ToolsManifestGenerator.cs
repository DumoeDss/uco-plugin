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
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using com.AtelierAI.Unity.Copilot.Editor.Branding;
using com.AtelierAI.Uco.Framework;
using UnityEditor;
using UnityEngine;

namespace com.AtelierAI.Unity.Copilot.Editor.Utils
{
    /// <summary>
    /// Generates <c>tools-manifest.json</c> — a static snapshot of the plugin's
    /// tool catalog in the SAME shape as the server's <c>GET /api/tools</c>
    /// response. The manifest ships inside the package so that <c>cocli init</c>
    /// can generate eager skill reference docs offline (no running Unity Editor
    /// or Uco server required).
    /// <para>
    /// Each entry contains <c>name</c>, <c>enabled</c>, <c>title</c>,
    /// <c>description</c>, <c>inputSchema</c>, optionally <c>outputSchema</c>,
    /// and the four nullable safety-hint members
    /// — exactly matching <see cref="com.AtelierAI.Uco.Framework.Server.Api.DirectToolCallEndpoints"/>.
    /// The <c>enabled</c> field reflects the DEFAULT state from
    /// <see cref="UcoToolAttribute"/> (not per-project runtime overrides)
    /// so the shipped manifest is deterministic across projects.
    /// </para>
    /// <para>
    /// Menu: <b>Tools / Unity Copilot / Generate Tools Manifest</b>.
    /// Requires an embedded or local-copy package installation (writes into
    /// <c>Packages/com.atelierai.unity.copilot/</c>).
    /// </para>
    /// </summary>
    public static class ToolsManifestGenerator
    {
        public const string ManifestFileName = "tools-manifest.json";
        static readonly HashSet<string> ExcludedSiblingReleaseTools = new(StringComparer.Ordinal)
        {
            "editor-application-request-close",
            "type-list-members"
        };

        [MenuItem(ProductInfo.ToolsMenuRoot + "/Generate Tools Manifest", priority = 2100)]
        public static void GenerateManifest()
        {
            // Ensure the plugin is built — it normally is via [InitializeOnLoad].
            var plugin = UnityCopilotPluginEditor.CurrentPlugin;
            if (plugin == null)
            {
                UnityCopilotPluginEditor.Instance.BuildUcoPluginIfNeeded();
                plugin = UnityCopilotPluginEditor.CurrentPlugin;
            }
            if (plugin == null)
            {
                Debug.LogError(
                    $"{ProductInfo.LogPrefix} Failed to build Uco plugin; cannot generate tools manifest.");
                return;
            }

            var toolManager = plugin.UcoManager?.ToolManager;
            if (toolManager == null)
            {
                Debug.LogError(
                    $"{ProductInfo.LogPrefix} ToolManager is null; cannot generate tools manifest.");
                return;
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
            var toolCount = GenerateManifest(toolManager.GetAllTools(), manifestPath);

            AssetDatabase.Refresh();
            Debug.Log(
                $"{ProductInfo.LogPrefix} Tools manifest generated: {manifestPath} ({toolCount} tools)");
        }

        [MenuItem(ProductInfo.ToolsMenuRoot + "/Generate Tools Manifest", validate = true)]
        public static bool ValidateGenerateManifest()
        {
            // Only show the menu when the editor singleton is available.
            return UnityCopilotPluginEditor.HasInstance;
        }

        internal static JsonArray BuildToolsArray(
            IEnumerable<IRunTool> tools,
            bool controlledRelease = false)
        {
            var toolsArray = new JsonArray();
            foreach (var tool in tools
                         .Where(tool => !controlledRelease
                             || !ExcludedSiblingReleaseTools.Contains(tool.Name))
                         .OrderBy(t => t.Name, StringComparer.Ordinal))
            {
                var entry = new JsonObject
                {
                    ["name"] = tool.Name,
                    ["enabled"] = ResolveDefaultEnabled(tool),
                    ["readOnlyHint"] = JsonValue.Create(tool.ReadOnlyHint),
                    ["destructiveHint"] = JsonValue.Create(tool.DestructiveHint),
                    ["idempotentHint"] = JsonValue.Create(tool.IdempotentHint),
                    ["openWorldHint"] = JsonValue.Create(tool.OpenWorldHint),
                    ["executionAffinity"] = (tool.ExecutionScheduling?.ExecutionAffinity
                        ?? ToolExecutionAffinity.MainThread).ToWireValue(),
                    ["threadSafeRead"] = tool.ExecutionScheduling?.ThreadSafeRead == true,
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
            return toolsArray;
        }

        internal static int GenerateManifest(IEnumerable<IRunTool> tools, string manifestPath)
        {
            var toolsArray = BuildToolsArray(tools, controlledRelease: true);
            var jsonOptions = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(
                manifestPath,
                toolsArray.ToJsonString(jsonOptions) + "\n",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return toolsArray.Count;
        }

        /// <summary>
        /// Resolves the DEFAULT enabled state from the <c>[UcoTool]</c>
        /// attribute — NOT the runtime-overridden state. This ensures the
        /// shipped manifest reflects plugin defaults rather than per-project
        /// config overrides applied by <c>ApplyConfigToUcoPlugin</c>.
        /// </summary>
        static bool ResolveDefaultEnabled(IRunTool tool)
        {
            // IRunTool.Method exposes the underlying MethodInfo.
            var attr = tool.Method?.GetCustomAttribute<UcoToolAttribute>();
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
