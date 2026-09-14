/*
 * Design inspired by MCP for Unity (CoplayDev/unity-mcp), Copyright (c) Coplay Inc., MIT License.
 * https://github.com/CoplayDev/unity-mcp/blob/main/MCPForUnity/Editor/Tools/ManageUI.cs
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
using System.IO;
using com.IvanMurzak.McpPlugin;
using com.IvanMurzak.ReflectorNet.Utils;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    public partial class Tool_UI
    {
        public const string UssCreateToolId = "ui-uss-create";

        [McpPluginTool
        (
            UssCreateToolId,
            Title = "UI / USS / Create",
            DestructiveHint = true
        )]
        [McpPluginSkillDescription("Write a new USS file under 'Assets/'. USS is treated as opaque text — no CSS AST " +
            "validation is performed. Refuses to overwrite an existing asset unless 'overwrite=true' is provided. " +
            "Pair with '" + UssReadToolId + "' and '" + UssModifyToolId + "' for incremental edits.")]
        [McpPluginSkillBody("Create a new USS (UI Toolkit Style Sheet) asset.\n\n" +
            "## Inputs\n\n" +
            "- `path` — required asset path under 'Assets/' ending in '.uss'. Intermediate folders are created.\n" +
            "- `content` — full USS document as a string. Must not be null.\n" +
            "- `overwrite` (default false) — when false and the asset already exists, returns `Ok=false`.\n\n" +
            "## Behavior\n\n" +
            "Writes the file then triggers `AssetDatabase.ImportAsset` + `Refresh`. No CSS / USS syntax validation " +
            "is performed — Unity's USS importer will flag any errors in the Console after import.")]
        [Description("Create a new USS asset under 'Assets/'.")]
        public UssCreateResult CreateUss
        (
            [Description("Asset path under 'Assets/'. Must end with '.uss'.")]
            string path,
            [Description("Full USS content (CSS-like text).")]
            string content,
            [Description("Force overwrite if the asset already exists. Default false.")]
            bool overwrite = false
        )
        {
            ValidateAssetPathUss(path, nameof(path));
            if (content == null)
                throw new ArgumentException(Error.UssContentNull(), nameof(content));

            return MainThread.Instance.Run(() =>
            {
                string absPath = ToAbsolutePath(path);

                if (File.Exists(absPath) && !overwrite)
                {
                    return new UssCreateResult
                    {
                        Ok = false,
                        AssetPath = path,
                        Error = Error.AssetAlreadyExists(path)
                    };
                }

                EnsureDirectoryExists(absPath);
                File.WriteAllText(absPath, content);
                RefreshAsset(path);

                return new UssCreateResult
                {
                    Ok = true,
                    AssetPath = path
                };
            });
        }
    }
}
