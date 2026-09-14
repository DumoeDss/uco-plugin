/*
 * Design inspired by MCP for Unity (CoplayDev/unity-mcp), Copyright (c) Coplay Inc., MIT License.
 * Original: https://github.com/CoplayDev/unity-mcp/blob/main/Server/src/services/tools/script_apply_edits.py
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
using System.Security.Cryptography;
using System.Text;
using com.IvanMurzak.McpPlugin;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    public static partial class Tool_Script
    {
        /// <summary>
        /// Structured result for <see cref="GetSha"/>. Designed so a single call gives the
        /// caller everything needed to (a) decide whether to re-read the file, and (b) feed
        /// the value back to <see cref="ApplyEdits"/> as `expectedSha`.
        /// </summary>
        public class ShaResult
        {
            public bool Ok { get; set; }
            public string FilePath { get; set; } = string.Empty;
            /// <summary>SHA-256 of the file's raw bytes, lower-case hex (no separators).</summary>
            public string? Sha { get; set; }
            /// <summary>Size of the file in bytes (matches `Sha` input length).</summary>
            public int? ByteSize { get; set; }
            public string? Error { get; set; }
        }

        public const string ScriptGetShaToolId = "script-get-sha";

        [McpPluginTool
        (
            ScriptGetShaToolId,
            Title = "Script / Get SHA",
            ReadOnlyHint = true,
            IdempotentHint = true,
            Enabled = false
        )]
        [McpPluginSkillDescription("Compute the SHA-256 of a `.cs` script file's raw bytes. " +
            "Returns the hash + byte size. Use this to capture an optimistic-lock token, then pass it as " +
            "`expectedSha` to '" + ScriptApplyEditsToolId + "' — the edit will be rejected with `STALE_SHA` if the file " +
            "changed between read and write.")]
        [McpPluginSkillBody("Computes the SHA-256 hash of the raw bytes of a `.cs` file on disk and returns " +
            "the hex digest and byte size. Read-only. Designed as the companion to '" + ScriptApplyEditsToolId + "' " +
            "for optimistic concurrency control.\n\n" +
            "## Inputs\n\n" +
            "- `filePath` — required project-relative `.cs` path (e.g. `Assets/Scripts/Player.cs`).\n\n" +
            "## Output\n\n" +
            "- `Ok` — true when the file was read successfully.\n" +
            "- `FilePath` — echoed input.\n" +
            "- `Sha` — lower-case hex SHA-256 of the raw bytes (no separators, no leading `0x`).\n" +
            "- `ByteSize` — file size in bytes.\n" +
            "- `Error` — set when `Ok=false` (file missing, not a `.cs` path, IO failure).\n\n" +
            "## Notes\n\n" +
            "- Computes the hash over the raw on-disk bytes (NOT a normalised text form). CRLF vs LF and BOM " +
            "presence will produce different hashes — by design, since '" + ScriptApplyEditsToolId + "' also " +
            "compares against the raw on-disk bytes when checking `expectedSha`.\n" +
            "- Does not touch the Unity AssetDatabase or run on the main thread.")]
        [Description("Compute the SHA-256 hash of a script file's raw bytes (hex, lower-case). " +
            "Pair with '" + ScriptApplyEditsToolId + "' as an optimistic-lock token.")]
        public static ShaResult GetSha
        (
            [Description("Project-relative path to the .cs file. Sample: \"Assets/Scripts/MyScript.cs\".")]
            string filePath
        )
        {
            // Defensive path validation — same shape as Script.Read.cs so error messages stay consistent.
            if (string.IsNullOrEmpty(filePath))
                return new ShaResult { Ok = false, FilePath = filePath ?? string.Empty, Error = Error.ScriptPathIsEmpty() };

            if (!filePath.EndsWith(".cs"))
                return new ShaResult { Ok = false, FilePath = filePath, Error = Error.FilePathMustEndsWithCs() };

            if (!File.Exists(filePath))
                return new ShaResult { Ok = false, FilePath = filePath, Error = Error.ScriptFileNotFound(filePath) };

            try
            {
                // Hash raw bytes — do not normalise line endings or strip BOM. The companion
                // ApplyEdits tool re-reads the same raw bytes when checking expectedSha, so any
                // text normalisation here would silently break the optimistic-lock contract.
                var bytes = File.ReadAllBytes(filePath);
                var sha = ComputeSha256Hex(bytes);
                return new ShaResult
                {
                    Ok = true,
                    FilePath = filePath,
                    Sha = sha,
                    ByteSize = bytes.Length,
                };
            }
            catch (Exception ex)
            {
                // IOException, UnauthorizedAccessException, SecurityException, etc. — surface a single
                // failure rather than throwing, so the caller can branch on the result instead of catching.
                return new ShaResult
                {
                    Ok = false,
                    FilePath = filePath,
                    Error = $"Failed to read '{filePath}': {ex.GetType().Name}: {ex.Message}",
                };
            }
        }

        /// <summary>
        /// SHA-256(bytes) -> lower-case hex string (no separators). Used by both
        /// <see cref="GetSha"/> and <see cref="ApplyEdits"/>; defined here so both tools
        /// share an exact bit-for-bit formatting (any divergence would break the SHA lock).
        /// </summary>
        internal static string ComputeSha256Hex(byte[] bytes)
        {
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(bytes);
            // Manual hex encode beats BitConverter.ToString().Replace("-","").ToLower() — fewer
            // allocations and no culture-sensitivity. Lower-case to match git / coplaydev output.
            var sb = new StringBuilder(hash.Length * 2);
            for (int i = 0; i < hash.Length; i++)
                sb.Append(hash[i].ToString("x2"));
            return sb.ToString();
        }
    }
}
