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
using System.IO;
using com.AtelierAI.Uco.Framework;
using com.AtelierAI.Uco.Framework.Common;
using com.AtelierAI.Uco.Framework.Common.Model;
using com.IvanMurzak.ReflectorNet.Utils;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

namespace AIGD
{
    [Description("Snapshot of the Unity Editor runtime state (play mode, compilation, version, paths).")]
    public class EditorStateData
    {
        [Description("True when the Editor is currently in Play Mode (EditorApplication.isPlaying).")]
        public bool IsPlayMode { get; set; }

        [Description("True when Play Mode is paused (EditorApplication.isPaused).")]
        public bool IsPaused { get; set; }

        [Description("True while a script compilation pass is in flight (EditorApplication.isCompiling).")]
        public bool IsCompiling { get; set; }

        [Description("True while the Editor is refreshing the AssetDatabase (EditorApplication.isUpdating).")]
        public bool IsUpdating { get; set; }

        [Description("Unity engine version string (Application.unityVersion).")]
        public string UnityVersion { get; set; } = string.Empty;

        [Description("Absolute path to the project root (parent of Application.dataPath).")]
        public string ProjectPath { get; set; } = string.Empty;

        [Description("Scripting backend currently selected for the active build target (Mono2x, IL2CPP, ...).")]
        public string ScriptingBackend { get; set; } = string.Empty;

        [Description("Active build target as configured in EditorUserBuildSettings.activeBuildTarget.")]
        public string ActiveBuildTarget { get; set; } = string.Empty;

        [Description("Build target group currently selected in the Editor (selectedBuildTargetGroup).")]
        public string SelectedBuildTargetGroup { get; set; } = string.Empty;
    }
}

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    using AIGD;
    using Consts = com.AtelierAI.Uco.Framework.Common.Consts;

    [UcoResourceType]
    public partial class Resource_EditorState
    {
        public const string EditorStateResourceUri = "editor://state";

        [UcoResource
        (
            Name = "Editor State",
            Route = EditorStateResourceUri,
            MimeType = Consts.MimeType.TextJson,
            ListResources = nameof(ListAll),
            Description = "Current Unity Editor state: play / pause / compilation flags, engine version, project path, " +
                "scripting backend and active build target. Use it to gate operations that require a specific Editor mode.",
            Enabled = true
        )]
        public ResponseResourceContent[] GetState(string uri)
        {
            return MainThread.Instance.Run(() =>
            {
                var data = CollectState();
                var json = SerializeState(data);
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
                uri: EditorStateResourceUri,
                name: "Editor State",
                enabled: true,
                mimeType: Consts.MimeType.TextJson)
        };

        // ---------------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------------

        private static EditorStateData CollectState()
        {
            var selectedGroup = EditorUserBuildSettings.selectedBuildTargetGroup;
            string scriptingBackend;
            try
            {
                // Unity 2021.2+ supports NamedBuildTarget. Prefer it over the deprecated
                // GetScriptingBackend(BuildTargetGroup) overload to avoid CS0618 warnings on 6.x.
                var namedTarget = NamedBuildTarget.FromBuildTargetGroup(selectedGroup);
                scriptingBackend = PlayerSettings.GetScriptingBackend(namedTarget).ToString();
            }
            catch
            {
                // Fallback for build target groups that NamedBuildTarget rejects (e.g. Unknown).
#pragma warning disable CS0618 // Obsolete overload — only used as last resort.
                scriptingBackend = PlayerSettings.GetScriptingBackend(selectedGroup).ToString();
#pragma warning restore CS0618
            }

            return new EditorStateData
            {
                IsPlayMode = EditorApplication.isPlaying,
                IsPaused = EditorApplication.isPaused,
                IsCompiling = EditorApplication.isCompiling,
                IsUpdating = EditorApplication.isUpdating,
                UnityVersion = Application.unityVersion,
                ProjectPath = Path.GetDirectoryName(Application.dataPath) ?? string.Empty,
                ScriptingBackend = scriptingBackend,
                ActiveBuildTarget = EditorUserBuildSettings.activeBuildTarget.ToString(),
                SelectedBuildTargetGroup = selectedGroup.ToString(),
            };
        }

        private static string SerializeState(EditorStateData d)
        {
            var sb = new System.Text.StringBuilder(256);
            sb.Append('{');
            sb.Append("\"isPlayMode\":").Append(d.IsPlayMode ? "true" : "false");
            sb.Append(",\"isPaused\":").Append(d.IsPaused ? "true" : "false");
            sb.Append(",\"isCompiling\":").Append(d.IsCompiling ? "true" : "false");
            sb.Append(",\"isUpdating\":").Append(d.IsUpdating ? "true" : "false");
            sb.Append(",\"unityVersion\":"); ResourceJson.AppendString(sb, d.UnityVersion);
            sb.Append(",\"projectPath\":"); ResourceJson.AppendString(sb, d.ProjectPath);
            sb.Append(",\"scriptingBackend\":"); ResourceJson.AppendString(sb, d.ScriptingBackend);
            sb.Append(",\"activeBuildTarget\":"); ResourceJson.AppendString(sb, d.ActiveBuildTarget);
            sb.Append(",\"selectedBuildTargetGroup\":"); ResourceJson.AppendString(sb, d.SelectedBuildTargetGroup);
            sb.Append('}');
            return sb.ToString();
        }
    }

    /// <summary>
    /// Shared minimal JSON helpers for resource payloads — kept here next to the first consumer.
    /// Mirrors the inline helpers in Resource/MenuItems.cs to avoid a Newtonsoft / System.Text.Json
    /// configuration dependency from the resources layer.
    /// </summary>
    internal static class ResourceJson
    {
        public static void AppendString(System.Text.StringBuilder sb, string? value)
        {
            if (value == null)
            {
                sb.Append("null");
                return;
            }
            sb.Append('"');
            foreach (var c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
