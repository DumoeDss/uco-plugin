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
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using com.AtelierAI.Uco.Framework;
using com.IvanMurzak.ReflectorNet.Utils;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using UnityEditor;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    public static partial class Tool_Script
    {
        /// <summary>
        /// LSP-style line/column range (zero-based, half-open).
        ///
        /// Semantics match VS Code's <c>Range</c>:
        ///   - <c>StartLine</c>/<c>EndLine</c> are 0-based.
        ///   - <c>StartColumn</c>/<c>EndColumn</c> are 0-based UTF-16 code unit offsets
        ///     into the line (NOT byte offsets, NOT grapheme clusters). This matches what
        ///     Roslyn and every common editor uses for `Position.Character`.
        ///   - End is exclusive: a zero-width range with Start == End is a pure insert.
        /// </summary>
        public class Range
        {
            [Description("0-based start line.")]
            public int StartLine { get; set; }
            [Description("0-based start column (UTF-16 code unit offset within the line).")]
            public int StartColumn { get; set; }
            [Description("0-based end line.")]
            public int EndLine { get; set; }
            [Description("0-based end column (UTF-16 code unit offset within the line, exclusive).")]
            public int EndColumn { get; set; }
        }

        /// <summary>
        /// A single text edit. Multiple edits are applied in reverse-sorted order, so
        /// callers do NOT need to compensate later ranges for earlier insertions/deletions.
        /// </summary>
        public class TextEdit
        {
            [Description("Range to replace. 0-based positions, end column is exclusive.")]
            public Range Range { get; set; } = new Range();
            [Description("New text to insert in place of the range. Can be empty (deletion) or multiline.")]
            public string NewText { get; set; } = string.Empty;
        }

        /// <summary>
        /// Structured outcome of <see cref="ApplyEdits"/>. Designed so the caller can
        /// branch on <see cref="Error"/> as a stable code (machine-parseable) while still
        /// surfacing a human-readable message for logs.
        ///
        /// Error codes:
        ///   - <c>STALE_SHA</c>    — `expectedSha` did not match the current file. Caller
        ///                          should re-fetch via <c>script-get-sha</c> + <c>script-read</c>
        ///                          (or use the <c>CurrentContent</c> returned here) and retry.
        ///   - <c>INVALID_PATH</c> — path missing, wrong extension, or outside Assets/Packages.
        ///   - <c>NOT_FOUND</c>    — file does not exist on disk.
        ///   - <c>INVALID_RANGE</c>— at least one edit's range was malformed or out of file bounds.
        ///   - <c>NO_EDITS</c>     — `edits` was null or empty.
        ///   - <c>SYNTAX_ERROR</c> — Roslyn parsed the post-edit text and reported errors.
        ///   - <c>IO_ERROR</c>     — read/write to disk failed.
        /// </summary>
        public class EditResult
        {
            public bool Ok { get; set; }
            public string FilePath { get; set; } = string.Empty;
            /// <summary>Machine-readable code; null on success.</summary>
            public string? Error { get; set; }
            /// <summary>Human-readable error detail. Diagnostic list for SYNTAX_ERROR.</summary>
            public string? ErrorDetail { get; set; }
            /// <summary>SHA-256 of the file *after* the write on success, or *current* SHA on STALE_SHA.</summary>
            public string? CurrentSha { get; set; }
            /// <summary>Current file content. Populated on STALE_SHA so the caller can re-derive edits without an extra read.</summary>
            public string? CurrentContent { get; set; }
            /// <summary>Roslyn diagnostics when Error=SYNTAX_ERROR. Empty otherwise.</summary>
            public ValidationDiagnostic[] Diagnostics { get; set; } = Array.Empty<ValidationDiagnostic>();
        }

        public const string ScriptApplyEditsToolId = "script-apply-edits";

        // Hard cap to guard against runaway input. 8 MiB of source is wildly more than any
        // sane C# file would need; keeps us out of accidental OOM on bad client input.
        private const int MaxApplyEditsFileBytes = 8 * 1024 * 1024;

        [UcoTool
        (
            ScriptApplyEditsToolId,
            Title = "Script / Apply Edits",
            DestructiveHint = true,
            OpenWorldHint = false,
            Enabled = false
        )]
        [UcoSkillDescription("LSP-style line/column ranged edits to a `.cs` file. Send only the diff " +
            "(an array of `{ Range, NewText }`) instead of re-uploading the entire file. " +
            "Optional `expectedSha` provides optimistic concurrency control — the edit is rejected with `STALE_SHA` " +
            "if the file changed between read and write. Use '" + ScriptGetShaToolId + "' to capture the SHA " +
            "and '" + ScriptReadToolId + "' to inspect content. Validates the post-edit text with Roslyn before writing.")]
        [UcoSkillBody("Applies a batch of LSP-style ranged text edits to a `.cs` script. " +
            "Designed for diff-only updates so an LLM doesn't have to round-trip the whole file.\n\n" +
            "## Inputs\n\n" +
            "- `filePath` — required project-relative `.cs` path. Must exist; must start with `Assets/` or `Packages/`.\n" +
            "- `edits` — required non-empty array. Each entry is `{ Range: { StartLine, StartColumn, EndLine, EndColumn }, NewText }`.\n" +
            "  - All line/column values are **0-based**. End column is **exclusive**.\n" +
            "  - Columns are UTF-16 code-unit offsets within the line (Roslyn / LSP / VS Code convention).\n" +
            "  - Edits MAY overlap in the source array — the tool sorts them by start position descending and applies in reverse, so the caller does NOT need to re-base later ranges.\n" +
            "  - Truly overlapping ranges (after sorting) are detected and rejected with `INVALID_RANGE`.\n" +
            "- `expectedSha` — optional SHA-256 hex (from '" + ScriptGetShaToolId + "'). When provided and mismatched, the call returns `STALE_SHA` with the current file's SHA and content for the caller to re-derive.\n" +
            "- `validate` — when true (default), parse the post-edit text with Roslyn and abort the write on any `Error`-severity diagnostic.\n\n" +
            "## Behavior\n\n" +
            "1. Validates the path (`Assets/` or `Packages/` only, `.cs` extension, exists on disk).\n" +
            "2. Reads the file as raw bytes; preserves original line endings (CRLF/LF/CR are NOT normalised).\n" +
            "3. If `expectedSha` is set, compares against the SHA-256 of the raw bytes. Mismatch returns `STALE_SHA`.\n" +
            "4. Sorts edits by `(StartLine desc, StartColumn desc)` and applies them sequentially from the end of the file backwards.\n" +
            "5. If `validate=true`, runs `CSharpSyntaxTree.ParseText` and aborts with `SYNTAX_ERROR` + diagnostics on any error-severity diagnostic.\n" +
            "6. Writes the new text back. Switches to the Unity main thread to call `AssetDatabase.ImportAsset` + `AssetDatabase.Refresh` so script changes are picked up by the Editor.\n" +
            "7. Returns the new SHA-256 + the file path.\n\n" +
            "## Notes\n\n" +
            "- Line endings are preserved exactly as-is on disk; the edit applies to characters between line positions, not to abstract \"lines\".\n" +
            "- An edit at column N where N equals the line length inserts at end-of-line; an edit at column 0 on line L+1 inserts at start-of-next-line.\n" +
            "- Roslyn parse is pure CPU and does NOT require the Unity main thread.\n" +
            "- Use '" + ScriptValidateToolId + "' for a pre-flight Roslyn check (Syntax/Semantic/Strict) before sending edits, if you want to be defensive.")]
        [Description("Apply LSP-style line/column ranged edits to a .cs file. Send only the diff, not the whole file. " +
            "Optional SHA-based optimistic locking; Roslyn syntax validation before write.")]
        public static EditResult ApplyEdits
        (
            [Description("Project-relative path to the .cs file. Must exist. Sample: \"Assets/Scripts/MyScript.cs\".")]
            string filePath,
            [Description("Array of LSP-style edits. Each is { Range, NewText }. Applied in reverse-sorted order — caller need not compensate.")]
            TextEdit[] edits,
            [Description("Optional SHA-256 (hex, lower-case) of file content captured before edits via '" + ScriptGetShaToolId + "'. If set and mismatched, returns STALE_SHA without writing.")]
            string? expectedSha = null,
            [Description("Validate the post-edit text via Roslyn syntax parse before writing. Default true.")]
            bool validate = true
        )
        {
            // ---- 1. Path validation (cheap, do first) ---------------------------
            if (string.IsNullOrEmpty(filePath))
                return Fail(filePath ?? string.Empty, "INVALID_PATH", Error.ScriptPathIsEmpty());

            if (!filePath.EndsWith(".cs", StringComparison.Ordinal))
                return Fail(filePath, "INVALID_PATH", Error.FilePathMustEndsWithCs());

            // Restrict writes to the project tree. Without this guard a malicious / mistaken
            // input like "../../../etc/passwd.cs" would be writable. Forward-slash normalise
            // before checking so backslash paths from Windows clients are not bypassable.
            var normalisedPath = filePath.Replace('\\', '/');
            if (normalisedPath.Contains("..")
                || !(normalisedPath.StartsWith("Assets/", StringComparison.Ordinal)
                     || normalisedPath.StartsWith("Packages/", StringComparison.Ordinal)))
            {
                return Fail(filePath, "INVALID_PATH",
                    "filePath must be a project-relative path under 'Assets/' or 'Packages/' (no parent-traversal).");
            }

            if (!File.Exists(filePath))
                return Fail(filePath, "NOT_FOUND", Error.ScriptFileNotFound(filePath));

            // ---- 2. Edit-array shape validation --------------------------------
            if (edits == null || edits.Length == 0)
                return Fail(filePath, "NO_EDITS", "edits must be a non-empty array.");

            // ---- 3. Read file & SHA check --------------------------------------
            byte[] originalBytes;
            try
            {
                var info = new FileInfo(filePath);
                if (info.Length > MaxApplyEditsFileBytes)
                    return Fail(filePath, "IO_ERROR",
                        $"File too large ({info.Length} bytes); limit {MaxApplyEditsFileBytes} bytes.");
                originalBytes = File.ReadAllBytes(filePath);
            }
            catch (Exception ex)
            {
                return Fail(filePath, "IO_ERROR", $"Failed to read file: {ex.GetType().Name}: {ex.Message}");
            }

            var currentSha = ComputeSha256Hex(originalBytes);

            if (!string.IsNullOrEmpty(expectedSha)
                && !string.Equals(expectedSha, currentSha, StringComparison.OrdinalIgnoreCase))
            {
                // STALE_SHA — return enough info for the caller to recover without another tool call.
                // We deliberately include CurrentContent because the typical recovery is to re-derive
                // edits against the new content, and a second read round-trip is wasteful.
                string currentContent;
                try { currentContent = DecodeUtf8(originalBytes); }
                catch { currentContent = string.Empty; }

                return new EditResult
                {
                    Ok = false,
                    FilePath = filePath,
                    Error = "STALE_SHA",
                    ErrorDetail = $"Expected SHA {expectedSha} but current SHA is {currentSha}. File changed between read and apply.",
                    CurrentSha = currentSha,
                    CurrentContent = currentContent,
                };
            }

            // ---- 4. Apply edits (CPU-only, no Unity API yet) -------------------
            string originalText;
            try
            {
                originalText = DecodeUtf8(originalBytes);
            }
            catch (Exception ex)
            {
                return Fail(filePath, "IO_ERROR", $"Failed to decode file as UTF-8: {ex.Message}");
            }

            // Pre-compute line start offsets so range -> char-offset is O(1) per edit
            // instead of O(N) per edit. Crucial when many edits are passed against a large file.
            int[] lineStartOffsets = ComputeLineStartOffsets(originalText);
            // The "logical line count" the caller can reference is one more than the number of
            // newlines (i.e. a trailing newline still has a valid "next" line at column 0).
            int maxLineInclusive = lineStartOffsets.Length - 1;

            // Validate every range against the file before applying any of them — atomic semantics.
            var resolved = new ResolvedEdit[edits.Length];
            for (int i = 0; i < edits.Length; i++)
            {
                var e = edits[i];
                if (e == null || e.Range == null)
                    return Fail(filePath, "INVALID_RANGE", $"edits[{i}] is null or has a null Range.");

                var r = e.Range;
                if (r.StartLine < 0 || r.StartColumn < 0 || r.EndLine < 0 || r.EndColumn < 0)
                    return Fail(filePath, "INVALID_RANGE", $"edits[{i}] has negative line/column.");
                if (r.EndLine < r.StartLine)
                    return Fail(filePath, "INVALID_RANGE", $"edits[{i}] EndLine ({r.EndLine}) < StartLine ({r.StartLine}).");
                if (r.EndLine == r.StartLine && r.EndColumn < r.StartColumn)
                    return Fail(filePath, "INVALID_RANGE",
                        $"edits[{i}] EndColumn ({r.EndColumn}) < StartColumn ({r.StartColumn}) on same line.");

                if (r.StartLine > maxLineInclusive)
                    return Fail(filePath, "INVALID_RANGE",
                        $"edits[{i}] StartLine ({r.StartLine}) is past end of file (max line is {maxLineInclusive}).");
                if (r.EndLine > maxLineInclusive)
                    return Fail(filePath, "INVALID_RANGE",
                        $"edits[{i}] EndLine ({r.EndLine}) is past end of file (max line is {maxLineInclusive}).");

                int startOffset = LineColToOffset(originalText, lineStartOffsets, r.StartLine, r.StartColumn, out var startErr);
                if (startErr != null)
                    return Fail(filePath, "INVALID_RANGE", $"edits[{i}] start: {startErr}");
                int endOffset = LineColToOffset(originalText, lineStartOffsets, r.EndLine, r.EndColumn, out var endErr);
                if (endErr != null)
                    return Fail(filePath, "INVALID_RANGE", $"edits[{i}] end: {endErr}");

                resolved[i] = new ResolvedEdit
                {
                    OriginalIndex = i,
                    StartOffset = startOffset,
                    EndOffset = endOffset,
                    NewText = e.NewText ?? string.Empty,
                };
            }

            // Apply in reverse-sorted order so earlier offsets don't shift later ones.
            // Sort key: StartOffset DESC, then EndOffset DESC (longer ranges first if same start).
            Array.Sort(resolved, (a, b) =>
            {
                int cmp = b.StartOffset.CompareTo(a.StartOffset);
                if (cmp != 0) return cmp;
                return b.EndOffset.CompareTo(a.EndOffset);
            });

            // Reject overlapping ranges. Two edits overlap if one's range contains a position
            // strictly inside another's range. After sorting StartOffset DESC, the next edit's
            // EndOffset must be <= the current edit's StartOffset (touching is OK — zero-width
            // insert at the same boundary is treated as adjacent, not overlapping).
            for (int i = 0; i < resolved.Length - 1; i++)
            {
                var hi = resolved[i];     // higher start offset
                var lo = resolved[i + 1]; // lower or equal start offset
                if (lo.EndOffset > hi.StartOffset)
                {
                    return Fail(filePath, "INVALID_RANGE",
                        $"edits[{lo.OriginalIndex}] overlaps edits[{hi.OriginalIndex}] " +
                        $"(offsets [{lo.StartOffset},{lo.EndOffset}) vs [{hi.StartOffset},{hi.EndOffset})).");
                }
            }

            // Build the new text. StringBuilder over substring concatenation — each edit is a
            // splice into the previous result, but doing them right-to-left over the ORIGINAL
            // string and accumulating means we never need to re-index.
            var sb = new StringBuilder(originalText.Length + 256);
            int cursor = originalText.Length;
            // Walk edits in start-offset DESC order: we already sorted that way.
            foreach (var ed in resolved)
            {
                // Append the unchanged suffix from `ed.EndOffset` up to the current cursor.
                if (cursor > ed.EndOffset)
                    sb.Insert(0, originalText.Substring(ed.EndOffset, cursor - ed.EndOffset));
                // Insert the replacement.
                if (ed.NewText.Length > 0)
                    sb.Insert(0, ed.NewText);
                cursor = ed.StartOffset;
            }
            // Prefix: anything before the leftmost edit's start.
            if (cursor > 0)
                sb.Insert(0, originalText.Substring(0, cursor));

            var newText = sb.ToString();

            // No-op short circuit. Avoids touching the AssetDatabase when nothing actually changed
            // (some clients may submit identity edits like `r.replace(r, "")` accidentally).
            if (string.Equals(newText, originalText, StringComparison.Ordinal))
            {
                return new EditResult
                {
                    Ok = true,
                    FilePath = filePath,
                    CurrentSha = currentSha,
                };
            }

            // ---- 5. Optional Roslyn validation ---------------------------------
            if (validate)
            {
                // Roslyn parse is pure CPU — does NOT require MainThread.
                var tree = CSharpSyntaxTree.ParseText(newText, path: filePath);
                var diags = tree.GetDiagnostics()
                    .Where(d => d.Severity == DiagnosticSeverity.Error)
                    .ToList();
                if (diags.Count > 0)
                {
                    return new EditResult
                    {
                        Ok = false,
                        FilePath = filePath,
                        Error = "SYNTAX_ERROR",
                        ErrorDetail = $"Post-edit text has {diags.Count} parser error(s). File was not written.",
                        CurrentSha = currentSha,
                        Diagnostics = diags.Select(MapDiagnosticToValidation).ToArray(),
                    };
                }
            }

            // ---- 6. Write & refresh --------------------------------------------
            byte[] newBytes;
            try
            {
                // Re-encode as UTF-8 *without BOM* if the original had no BOM; preserve BOM if it had one.
                // Standards: Unity's CS files commonly do not have a BOM; we replay whatever was on disk.
                newBytes = EncodeUtf8MatchingBom(newText, originalBytes);
                File.WriteAllBytes(filePath, newBytes);
            }
            catch (Exception ex)
            {
                return Fail(filePath, "IO_ERROR", $"Failed to write file: {ex.GetType().Name}: {ex.Message}");
            }

            var newSha = ComputeSha256Hex(newBytes);

            // AssetDatabase calls MUST run on the Unity main thread. Run synchronously so the
            // caller sees the refresh complete by the time we return — script-apply-edits is a
            // discrete, synchronous-feeling operation in the caller's mental model.
            try
            {
                MainThread.Instance.Run(() =>
                {
                    AssetDatabase.ImportAsset(filePath, ImportAssetOptions.ForceUpdate);
                    AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                });
            }
            catch (Exception ex)
            {
                // The file IS written at this point — surface the refresh failure but still
                // report the new SHA so the caller can correlate.
                return new EditResult
                {
                    Ok = false,
                    FilePath = filePath,
                    Error = "IO_ERROR",
                    ErrorDetail = $"File written but AssetDatabase refresh failed: {ex.GetType().Name}: {ex.Message}",
                    CurrentSha = newSha,
                };
            }

            return new EditResult
            {
                Ok = true,
                FilePath = filePath,
                CurrentSha = newSha,
            };
        }

        // -------------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------------

        /// <summary>
        /// Resolved form of a <see cref="TextEdit"/> — line/col already collapsed to
        /// absolute character offsets in the original text. <see cref="OriginalIndex"/>
        /// is preserved purely so error messages can quote the caller's edit index.
        /// </summary>
        struct ResolvedEdit
        {
            public int OriginalIndex;
            public int StartOffset;
            public int EndOffset;
            public string NewText;
        }

        /// <summary>
        /// Precompute the start offset of every line, including a virtual line just past EOF.
        ///
        /// Returns an array where `result[L]` is the character index where line L starts.
        /// `result.Length - 1` is therefore the maximum valid line index (the virtual line
        /// at EOF, used for inserts at end-of-file).
        ///
        /// Line counting rules:
        ///   - `\n` increments the line counter.
        ///   - `\r\n` is counted as ONE line (the `\r` does not advance; the `\n` does).
        ///   - Lone `\r` increments the line counter (rare but valid, matches Roslyn).
        /// </summary>
        static int[] ComputeLineStartOffsets(string text)
        {
            // Both passes use the SAME line-break rules to keep counts in sync:
            //   - \r\n    → one line break, consume both chars
            //   - lone \n → one line break
            //   - lone \r → one line break
            // The first pass just counts; the second records each line-start offset.
            int lineCount = 1;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\r')
                {
                    lineCount++;
                    if (i + 1 < text.Length && text[i + 1] == '\n')
                        i++; // consume \n half of CRLF
                }
                else if (c == '\n')
                {
                    lineCount++;
                }
            }

            // offsets[L] is the char index where line L starts.
            // offsets[lineCount] is the virtual EOF line — its start equals text.Length so
            // an insert at (lineCount, 0) appends at end-of-file.
            var offsets = new int[lineCount + 1];
            offsets[0] = 0;
            int line = 1;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\r')
                {
                    if (i + 1 < text.Length && text[i + 1] == '\n')
                    {
                        offsets[line++] = i + 2;
                        i++;
                    }
                    else
                    {
                        offsets[line++] = i + 1;
                    }
                }
                else if (c == '\n')
                {
                    offsets[line++] = i + 1;
                }
            }
            offsets[lineCount] = text.Length;
            return offsets;
        }

        /// <summary>
        /// Convert a 0-based (line, column) pair to an absolute character offset in
        /// <paramref name="text"/>. Returns -1 with <paramref name="error"/> populated if
        /// the column exceeds the line's content length.
        ///
        /// "Line length" here is the number of UTF-16 code units in the line up to but not
        /// including the next line break (so column == lineLength is the position
        /// immediately before the `\n`/`\r\n` — i.e. end-of-line insertion).
        /// </summary>
        static int LineColToOffset(string text, int[] lineStarts, int line, int column, out string? error)
        {
            error = null;
            if (line < 0 || line >= lineStarts.Length)
            {
                error = $"line {line} out of range [0,{lineStarts.Length - 1}]";
                return -1;
            }
            int start = lineStarts[line];

            // Compute the length of this line's *content* (excluding the trailing line break).
            // We walk forward from `start` until we hit \r, \n, or EOF — this is also our
            // sanity check against ComputeLineStartOffsets (they must agree).
            int contentEnd = start;
            while (contentEnd < text.Length)
            {
                char c = text[contentEnd];
                if (c == '\r' || c == '\n') break;
                contentEnd++;
            }
            int lineLength = contentEnd - start;
            if (column > lineLength)
            {
                error = $"column {column} exceeds line {line} length ({lineLength})";
                return -1;
            }
            return start + column;
        }

        /// <summary>
        /// Decode UTF-8 bytes (with or without BOM) to a .NET string. Strips a leading
        /// BOM if present so downstream offsets are not skewed by 3 invisible characters.
        /// </summary>
        static string DecodeUtf8(byte[] bytes)
        {
            // BOM = EF BB BF. Strip if present so all character offsets are content-relative.
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            return Encoding.UTF8.GetString(bytes);
        }

        /// <summary>
        /// Encode <paramref name="text"/> as UTF-8 bytes, preserving the BOM-presence
        /// state of <paramref name="originalBytes"/>. We never *add* or *remove* a BOM
        /// during an edit — keep what the user had.
        /// </summary>
        static byte[] EncodeUtf8MatchingBom(string text, byte[] originalBytes)
        {
            bool hadBom = originalBytes.Length >= 3
                && originalBytes[0] == 0xEF
                && originalBytes[1] == 0xBB
                && originalBytes[2] == 0xBF;

            if (hadBom)
            {
                var body = Encoding.UTF8.GetBytes(text);
                var withBom = new byte[body.Length + 3];
                withBom[0] = 0xEF; withBom[1] = 0xBB; withBom[2] = 0xBF;
                Buffer.BlockCopy(body, 0, withBom, 3, body.Length);
                return withBom;
            }
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text);
        }

        /// <summary>
        /// 1-based diagnostic mapper specific to this file. Mirrors the helper of the same
        /// shape in <c>Script.Validate.cs</c>; renamed locally because partial-class member
        /// names must be unique across files even when the signature matches.
        /// </summary>
        static ValidationDiagnostic MapDiagnosticToValidation(Diagnostic d)
        {
            var span = d.Location.GetLineSpan();
            return new ValidationDiagnostic
            {
                Severity = d.Severity.ToString(),
                Code = d.Id,
                Message = d.GetMessage(),
                Line = span.StartLinePosition.Line + 1,
                Column = span.StartLinePosition.Character + 1,
                EndLine = span.EndLinePosition.Line + 1,
                EndColumn = span.EndLinePosition.Character + 1,
            };
        }

        static EditResult Fail(string filePath, string code, string detail) => new EditResult
        {
            Ok = false,
            FilePath = filePath,
            Error = code,
            ErrorDetail = detail,
        };
    }
}
