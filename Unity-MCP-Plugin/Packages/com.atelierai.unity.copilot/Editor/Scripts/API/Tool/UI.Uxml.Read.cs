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
        public const string UxmlReadToolId = "ui-uxml-read";

        [McpPluginTool
        (
            UxmlReadToolId,
            Title = "UI / UXML / Read",
            ReadOnlyHint = true,
            IdempotentHint = true
        )]
        [McpPluginSkillDescription("Read a UXML file under 'Assets/' and return its raw XML content as a string. " +
            "Pair with '" + UxmlModifyToolId + "' for structural edits or '" + UxmlCreateToolId + "' to write a new file.")]
        [McpPluginSkillBody("Read a UXML asset's full XML content as a string.\n\n" +
            "## Inputs\n\n" +
            "- `path` — required asset path under 'Assets/' ending in '.uxml'.\n\n" +
            "## Behavior\n\n" +
            "Reads the file with `File.ReadAllText`. Returns `Ok=false` with an error when the file is missing. " +
            "No XML validation is performed on read — call '" + UxmlModifyToolId + "' or use the raw string yourself.")]
        [Description("Read UXML content as a string.")]
        public UxmlReadResult ReadUxml
        (
            [Description("Asset path under 'Assets/'. Must end with '.uxml'.")]
            string path
        )
        {
            ValidateAssetPathUxml(path, nameof(path));

            string absPath = ToAbsolutePath(path);
            if (!File.Exists(absPath))
            {
                return new UxmlReadResult
                {
                    Ok = false,
                    AssetPath = path,
                    Error = Error.AssetNotFound(path)
                };
            }

            try
            {
                var content = File.ReadAllText(absPath);
                return new UxmlReadResult
                {
                    Ok = true,
                    AssetPath = path,
                    Content = content
                };
            }
            catch (Exception ex)
            {
                return new UxmlReadResult
                {
                    Ok = false,
                    AssetPath = path,
                    Error = Error.FileReadFailed(path, ex.Message)
                };
            }
        }
    }
}
