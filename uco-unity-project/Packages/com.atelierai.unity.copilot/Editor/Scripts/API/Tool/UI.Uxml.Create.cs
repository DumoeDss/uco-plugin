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
using System.ComponentModel;
using System.IO;
using com.AtelierAI.Uco.Framework;
using com.IvanMurzak.ReflectorNet.Utils;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    public partial class Tool_UI
    {
        public const string UxmlCreateToolId = "ui-uxml-create";

        [UcoTool
        (
            UxmlCreateToolId,
            Title = "UI / UXML / Create",
            DestructiveHint = true
        )]
        [UcoSkillDescription("Write a new UXML file under 'Assets/'. Validates that 'content' is well-formed XML " +
            "via 'XDocument.Parse' before touching disk — invalid UXML is rejected with details. " +
            "Refuses to overwrite an existing asset unless 'overwrite=true' is provided. " +
            "Pair with '" + UxmlReadToolId + "' to inspect existing files and '" + UxmlModifyToolId + "' for structural edits.")]
        [UcoSkillBody("Create a new UXML (UI Toolkit visual tree) asset.\n\n" +
            "## Inputs\n\n" +
            "- `path` — required asset path under 'Assets/' ending in '.uxml'. Intermediate folders are created.\n" +
            "- `content` — full UXML document as a string. Must be parseable by `System.Xml.Linq.XDocument.Parse`.\n" +
            "- `overwrite` (default false) — when false and the asset already exists, returns `Ok=false`.\n\n" +
            "## Behavior\n\n" +
            "Parses the UXML on the calling thread (fast XML validation), then writes the file and triggers " +
            "`AssetDatabase.ImportAsset` + `Refresh` on the Unity main thread. Throws `ArgumentException` for empty " +
            "path / wrong extension / out-of-tree path / invalid XML.")]
        [Description("Create a new UXML asset under 'Assets/'. Validates XML before writing.")]
        public UxmlCreateResult CreateUxml
        (
            [Description("Asset path under 'Assets/'. Must end with '.uxml'.")]
            string path,
            [Description("Full UXML content (XML string).")]
            string content,
            [Description("Force overwrite if the asset already exists. Default false.")]
            bool overwrite = false
        )
        {
            // Fail fast on bad path / bad XML — these don't need the main thread.
            ValidateAssetPathUxml(path, nameof(path));
            ParseUxmlOrThrow(content, nameof(content));

            return MainThread.Instance.Run(() =>
            {
                string absPath = ToAbsolutePath(path);

                if (File.Exists(absPath) && !overwrite)
                {
                    return new UxmlCreateResult
                    {
                        Ok = false,
                        AssetPath = path,
                        Error = Error.AssetAlreadyExists(path)
                    };
                }

                EnsureDirectoryExists(absPath);
                File.WriteAllText(absPath, content);
                RefreshAsset(path);

                return new UxmlCreateResult
                {
                    Ok = true,
                    AssetPath = path
                };
            });
        }
    }
}
