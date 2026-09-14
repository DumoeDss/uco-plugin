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
using System.ComponentModel;
using com.IvanMurzak.McpPlugin;
using com.IvanMurzak.McpPlugin.Common;
using com.IvanMurzak.McpPlugin.Common.Model;
using com.IvanMurzak.ReflectorNet.Utils;
using UnityEditor;
using UnityEngine;

namespace AIGD
{
    [Description("Project-level identity (product/company/version/bundle id) and the well-known Unity paths.")]
    public class ProjectInfoData
    {
        [Description("PlayerSettings.productName — human-readable application name.")]
        public string ProductName { get; set; } = string.Empty;

        [Description("PlayerSettings.bundleVersion (Application.version).")]
        public string Version { get; set; } = string.Empty;

        [Description("PlayerSettings.companyName.")]
        public string CompanyName { get; set; } = string.Empty;

        [Description("Application.identifier — bundle identifier / package name for the active platform.")]
        public string BundleIdentifier { get; set; } = string.Empty;

        [Description("Absolute path to the project's Assets folder (Application.dataPath).")]
        public string DataPath { get; set; } = string.Empty;

        [Description("OS-specific temporary cache directory (Application.temporaryCachePath).")]
        public string TemporaryCachePath { get; set; } = string.Empty;

        [Description("OS-specific persistent data directory (Application.persistentDataPath).")]
        public string PersistentDataPath { get; set; } = string.Empty;

        [Description("StreamingAssets directory for the active platform (Application.streamingAssetsPath).")]
        public string StreamingAssetsPath { get; set; } = string.Empty;

        [Description("Unity engine version string (Application.unityVersion).")]
        public string UnityVersion { get; set; } = string.Empty;

        [Description("Editor-selected build target group (EditorUserBuildSettings.selectedBuildTargetGroup).")]
        public string PlatformGroup { get; set; } = string.Empty;
    }
}

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    using AIGD;
    using Consts = com.IvanMurzak.McpPlugin.Common.Consts;

    [McpPluginResourceType]
    public partial class Resource_ProjectInfo
    {
        public const string ProjectInfoResourceUri = "project://info";

        [McpPluginResource
        (
            Name = "Project Info",
            Route = ProjectInfoResourceUri,
            MimeType = Consts.MimeType.TextJson,
            ListResources = nameof(ListAll),
            Description = "Project identity (product / company / version / bundle id) plus the well-known Unity " +
                "directories (Assets, persistent, cache, StreamingAssets). Use it to disambiguate which project " +
                "the Editor session is operating on.",
            Enabled = true
        )]
        public ResponseResourceContent[] GetInfo(string uri)
        {
            return MainThread.Instance.Run(() =>
            {
                var data = CollectInfo();
                var json = SerializeInfo(data);
                return ResponseResourceContent.CreateText(
                    uri: uri,
                    mimeType: Consts.MimeType.TextJson,
                    text: json
                ).MakeArray();
            });
        }

        public ResponseListResource[] ListAll() => new[]
        {
            new ResponseListResource(
                uri: ProjectInfoResourceUri,
                name: "Project Info",
                enabled: true,
                mimeType: Consts.MimeType.TextJson)
        };

        // ---------------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------------

        private static ProjectInfoData CollectInfo() => new ProjectInfoData
        {
            ProductName = Application.productName,
            Version = Application.version,
            CompanyName = Application.companyName,
            BundleIdentifier = Application.identifier,
            DataPath = Application.dataPath,
            TemporaryCachePath = Application.temporaryCachePath,
            PersistentDataPath = Application.persistentDataPath,
            StreamingAssetsPath = Application.streamingAssetsPath,
            UnityVersion = Application.unityVersion,
            PlatformGroup = EditorUserBuildSettings.selectedBuildTargetGroup.ToString(),
        };

        private static string SerializeInfo(ProjectInfoData d)
        {
            var sb = new System.Text.StringBuilder(512);
            sb.Append('{');
            sb.Append("\"productName\":"); ResourceJson.AppendString(sb, d.ProductName);
            sb.Append(",\"version\":"); ResourceJson.AppendString(sb, d.Version);
            sb.Append(",\"companyName\":"); ResourceJson.AppendString(sb, d.CompanyName);
            sb.Append(",\"bundleIdentifier\":"); ResourceJson.AppendString(sb, d.BundleIdentifier);
            sb.Append(",\"dataPath\":"); ResourceJson.AppendString(sb, d.DataPath);
            sb.Append(",\"temporaryCachePath\":"); ResourceJson.AppendString(sb, d.TemporaryCachePath);
            sb.Append(",\"persistentDataPath\":"); ResourceJson.AppendString(sb, d.PersistentDataPath);
            sb.Append(",\"streamingAssetsPath\":"); ResourceJson.AppendString(sb, d.StreamingAssetsPath);
            sb.Append(",\"unityVersion\":"); ResourceJson.AppendString(sb, d.UnityVersion);
            sb.Append(",\"platformGroup\":"); ResourceJson.AppendString(sb, d.PlatformGroup);
            sb.Append('}');
            return sb.ToString();
        }
    }
}
