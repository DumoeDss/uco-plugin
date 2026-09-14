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
using System.Text;
using System.Text.RegularExpressions;
using com.AtelierAI.Uco.Framework;
using com.IvanMurzak.ReflectorNet.Utils;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    public partial class Tool_UI
    {
        public const string UssModifyToolId = "ui-uss-modify";

        [UcoTool
        (
            UssModifyToolId,
            Title = "UI / USS / Modify",
            DestructiveHint = true
        )]
        [UcoSkillDescription("Modify a USS file via simple text operations: append text, replace the whole " +
            "file, append a new rule, or remove an existing rule by selector. USS is treated as opaque text — no CSS " +
            "AST is built. The 'remove-rule' op uses a flat regex which is correct for simple rules but cannot " +
            "handle nested or pseudo-selector overlaps; see body for limits.")]
        [UcoSkillBody("Modify a USS file via text operations.\n\n" +
            "## Operations\n\n" +
            "- `append` — appends `content` (plus a trailing newline) to the end of the file.\n" +
            "- `replace-all` — overwrites the entire file with `content`.\n" +
            "- `add-rule` — appends `<selector> { <declarations> }` (formatted with newlines) to the end of the file.\n" +
            "- `remove-rule` — deletes the first rule whose selector matches `selector`, using the flat regex " +
            "`<escaped-selector>\\s*\\{[^}]*\\}`. The selector match is anchored to a complete selector token " +
            "(preceded by start-of-file, `}`, or whitespace) to avoid stripping rules whose selector is merely " +
            "a substring of another selector.\n\n" +
            "## Known limits\n\n" +
            "USS does not support nested rules, so `[^}]*` is normally safe. However, the regex cannot handle:\n" +
            "1. Strings containing literal `}` characters (very rare in real USS).\n" +
            "2. Selector groups that share the rule body — e.g. `.a, .b { ... }` will only match when you pass " +
            "the entire group `\".a, .b\"` exactly.\n" +
            "3. Comments containing `}` — these break the heuristic.\n\n" +
            "For non-trivial edits, prefer `replace-all` with content assembled by the caller.")]
        [Description("Modify a USS file via text operations: 'append', 'replace-all', 'add-rule', 'remove-rule'.")]
        public UssModifyResult ModifyUss
        (
            [Description("Asset path under 'Assets/'. Must end with '.uss'.")]
            string path,
            [Description("Operation: 'append' | 'replace-all' | 'add-rule' | 'remove-rule'.")]
            string operation,
            [Description("CSS selector for 'add-rule' / 'remove-rule'. Sample: '.button-primary' or '#header'.")]
            string? selector = null,
            [Description("Body for 'add-rule' (declarations without curly braces). " +
                "Sample: 'color: red; font-size: 14px;'.")]
            string? declarations = null,
            [Description("Content for 'append' or 'replace-all'.")]
            string? content = null
        )
        {
            ValidateAssetPathUss(path, nameof(path));

            if (string.IsNullOrEmpty(operation))
                throw new ArgumentException(Error.UnknownUssOperation(operation ?? "null"), nameof(operation));

            var op = operation.ToLowerInvariant();

            switch (op)
            {
                case "append":
                case "replace-all":
                    if (content == null)
                        throw new ArgumentException(Error.UssContentNull(), nameof(content));
                    break;

                case "add-rule":
                    if (string.IsNullOrEmpty(selector))
                        throw new ArgumentException(Error.SelectorRequired(op), nameof(selector));
                    if (string.IsNullOrEmpty(declarations))
                        throw new ArgumentException(Error.DeclarationsRequired(op), nameof(declarations));
                    break;

                case "remove-rule":
                    if (string.IsNullOrEmpty(selector))
                        throw new ArgumentException(Error.SelectorRequired(op), nameof(selector));
                    break;

                default:
                    throw new ArgumentException(Error.UnknownUssOperation(operation), nameof(operation));
            }

            return MainThread.Instance.Run(() =>
            {
                string absPath = ToAbsolutePath(path);

                // 'replace-all' is the only op that can create a brand-new file when the
                // asset is missing — every other op requires the existing file.
                bool fileExists = File.Exists(absPath);
                if (!fileExists && op != "replace-all")
                {
                    return new UssModifyResult
                    {
                        Ok = false,
                        AssetPath = path,
                        Error = Error.AssetNotFound(path)
                    };
                }

                string original = fileExists ? File.ReadAllText(absPath) : string.Empty;
                string updated = original;
                int affected = 0;

                switch (op)
                {
                    case "append":
                    {
                        var sb = new StringBuilder(original);
                        if (original.Length > 0 && !original.EndsWith("\n", StringComparison.Ordinal))
                            sb.Append('\n');
                        sb.Append(content);
                        if (!content!.EndsWith("\n", StringComparison.Ordinal))
                            sb.Append('\n');
                        updated = sb.ToString();
                        break;
                    }

                    case "replace-all":
                    {
                        updated = content!;
                        EnsureDirectoryExists(absPath);
                        break;
                    }

                    case "add-rule":
                    {
                        var sb = new StringBuilder(original);
                        if (original.Length > 0 && !original.EndsWith("\n", StringComparison.Ordinal))
                            sb.Append('\n');
                        sb.Append(selector).Append(" {\n");
                        // Indent each ';'-terminated declaration line for readability.
                        var trimmed = declarations!.Trim();
                        foreach (var rawLine in trimmed.Split('\n'))
                        {
                            var line = rawLine.Trim();
                            if (line.Length == 0)
                                continue;
                            sb.Append("    ").Append(line).Append('\n');
                        }
                        sb.Append("}\n");
                        updated = sb.ToString();
                        affected = 1;
                        break;
                    }

                    case "remove-rule":
                    {
                        // Anchor to a selector boundary so '.foo' does not match inside '.foo-bar'.
                        // Allowed prefix: start-of-string, '}', or whitespace.
                        // Allowed selector suffix: whitespace, ',', '{', or end-of-rule.
                        string escaped = Regex.Escape(selector!);
                        var pattern = new Regex(
                            @"(^|[\s\}])" + escaped + @"(?=[\s,\{])[^\{\}]*\{[^\}]*\}\s*",
                            RegexOptions.Multiline);

                        var match = pattern.Match(original);
                        if (!match.Success)
                        {
                            return new UssModifyResult
                            {
                                Ok = false,
                                AssetPath = path,
                                AffectedCount = 0,
                                Error = Error.SelectorNotFound(selector!)
                            };
                        }
                        // Preserve the boundary character captured by group 1 so we don't
                        // collapse two adjacent rules accidentally.
                        var preserve = match.Groups[1].Value;
                        updated = original.Substring(0, match.Index)
                                  + preserve
                                  + original.Substring(match.Index + match.Length);
                        affected = 1;
                        break;
                    }
                }

                try
                {
                    File.WriteAllText(absPath, updated);
                }
                catch (Exception ex)
                {
                    return new UssModifyResult
                    {
                        Ok = false,
                        AssetPath = path,
                        Error = $"Failed to write USS: {ex.Message}"
                    };
                }

                RefreshAsset(path);

                return new UssModifyResult
                {
                    Ok = true,
                    AssetPath = path,
                    AffectedCount = affected
                };
            });
        }
    }
}
