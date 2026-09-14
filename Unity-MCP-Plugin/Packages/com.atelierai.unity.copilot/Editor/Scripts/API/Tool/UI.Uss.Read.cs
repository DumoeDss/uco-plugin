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

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    public partial class Tool_UI
    {
        public const string UssReadToolId = "ui-uss-read";

        [McpPluginTool
        (
            UssReadToolId,
            Title = "UI / USS / Read",
            ReadOnlyHint = true,
            IdempotentHint = true
        )]
        [McpPluginSkillDescription("Read a USS file under 'Assets/' and return its raw text content. " +
            "Pair with '" + UssModifyToolId + "' for incremental edits or '" + UssCreateToolId + "' to write a new file.")]
        [McpPluginSkillBody("Read a USS asset's full text content as a string.\n\n" +
            "## Inputs\n\n" +
            "- `path` — required asset path under 'Assets/' ending in '.uss'.\n\n" +
            "## Behavior\n\n" +
            "Reads the file with `File.ReadAllText`. Returns `Ok=false` with an error when the file is missing.")]
        [Description("Read USS content as a string.")]
        public UssReadResult ReadUss
        (
            [Description("Asset path under 'Assets/'. Must end with '.uss'.")]
            string path
        )
        {
            ValidateAssetPathUss(path, nameof(path));

            string absPath = ToAbsolutePath(path);
            if (!File.Exists(absPath))
            {
                return new UssReadResult
                {
                    Ok = false,
                    AssetPath = path,
                    Error = Error.AssetNotFound(path)
                };
            }

            try
            {
                var content = File.ReadAllText(absPath);
                return new UssReadResult
                {
                    Ok = true,
                    AssetPath = path,
                    Content = content
                };
            }
            catch (Exception ex)
            {
                return new UssReadResult
                {
                    Ok = false,
                    AssetPath = path,
                    Error = Error.FileReadFailed(path, ex.Message)
                };
            }
        }
    }
}
