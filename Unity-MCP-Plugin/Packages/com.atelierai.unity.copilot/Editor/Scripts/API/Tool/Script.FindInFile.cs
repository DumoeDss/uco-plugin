/*
 * Design inspired by MCP for Unity (CoplayDev/unity-mcp), Copyright (c) Coplay Inc., MIT License.
 * https://github.com/CoplayDev/unity-mcp/blob/main/Server/src/services/tools/find_in_file.py
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
using System.Text.RegularExpressions;
using com.IvanMurzak.McpPlugin;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    public static partial class Tool_Script
    {
        public const string ScriptFindInFileToolId = "script-find-in-file";

        // Hard caps and defaults.
        const int MaxContextLines = 20;
        const int HardResultsCap = 2000;
        const int DefaultMaxResults = 200;
        const int DefaultMaxFileBytes = 5_000_000;
        static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(200);

        // Binary / non-text extensions to skip outright. Lower-case, with leading dot.
        static readonly HashSet<string> BinarySkipExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".meta",
            ".asset",
            ".unity",
            ".prefab",
            ".png", ".jpg", ".jpeg", ".tga", ".psd", ".bmp", ".gif", ".tif", ".tiff", ".exr", ".hdr", ".ico",
            ".dll", ".so", ".dylib", ".pdb", ".exe",
            ".fbx", ".obj", ".blend", ".3ds", ".dae",
            ".wav", ".mp3", ".ogg", ".aif", ".aiff", ".flac",
            ".mp4", ".mov", ".avi", ".webm", ".mkv",
            ".zip", ".rar", ".7z", ".gz", ".tar",
            ".ttf", ".otf",
            ".bin", ".pak"
        };

        [McpPluginTool
        (
            ScriptFindInFileToolId,
            Title = "Script / Find In File",
            ReadOnlyHint = true,
            IdempotentHint = true
        )]
        [McpPluginSkillDescription("Ripgrep-style text search over project files under `Assets/` or `Packages/`. " +
            "Streams files line-by-line with a configurable regex (or plain-text) pattern. " +
            "Supports glob filtering, case-insensitive match, context lines, and bounded result/file-size caps. " +
            "Pair with '" + ScriptReadToolId + "' to read the surrounding code once you find a hit.")]
        [McpPluginSkillBody("Recursively searches files under `Assets/` or `Packages/` for a regex or plain-text pattern. " +
            "Designed to behave like ripgrep — streams files, skips binaries by extension, caps file size, and bounds total results.\n\n" +
            "## Inputs\n\n" +
            "- `pattern` — required regex. When `plainText=true`, the pattern is escaped and matched literally.\n" +
            "- `root` — project-relative root. Default `Assets/`. Must start with `Assets` or `Packages`.\n" +
            "- `glob` — file filter glob. Default `*.cs`. Supports `*`, `**` (any depth), `?`. Matched against the project-relative path.\n" +
            "- `plainText` — when true, treat `pattern` as literal text (auto-`Regex.Escape`).\n" +
            "- `ignoreCase` — case-insensitive match. Default false.\n" +
            "- `context` — lines of context around each match. Default 0, clamped to 20.\n" +
            "- `maxResults` — total match cap across all files. Default 200, hard cap 2000.\n" +
            "- `maxFileBytes` — files larger than this are skipped without reading. Default 5_000_000.\n\n" +
            "## Behavior\n\n" +
            "- Regex compiled with a 200ms execution timeout. Files whose lines time out are skipped (counted as skipped).\n" +
            "- Binary/asset extensions (`.meta`, `.unity`, `.png`, `.dll`, `.fbx`, etc.) are skipped by extension.\n" +
            "- Hidden directories whose name starts with `.` are pruned.\n" +
            "- Returns `Truncated=true` once `maxResults` is hit; remaining files are not scanned.\n" +
            "- Output paths use forward-slash, project-relative form.")]
        [Description("Search files under Assets/ or Packages/ for a regex or plain-text pattern (ripgrep-style). " +
            "Returns matching lines with optional context.")]
        public static FindResult Find
        (
            [Description("Search pattern (regex by default, plain text when 'plainText'=true).")]
            string pattern,
            [Description("Root path relative to project. Default 'Assets/'. Use 'Packages/' to search packages.")]
            string? root = null,
            [Description("Glob to filter files, e.g. '*.cs', '**/*.uss'. Default '*.cs'.")]
            string? glob = null,
            [Description("Treat pattern as plain text (no regex metachars). Default false.")]
            bool plainText = false,
            [Description("Case-insensitive match. Default false.")]
            bool ignoreCase = false,
            [Description("Lines of context before/after each match. Default 0, max 20.")]
            int context = 0,
            [Description("Max matches returned across all files. Default 200, hard cap 2000.")]
            int maxResults = DefaultMaxResults,
            [Description("Max bytes per file to scan. Default 5_000_000. Files larger are skipped.")]
            int maxFileBytes = DefaultMaxFileBytes
        )
        {
            // ---- Argument validation -------------------------------------------------
            if (string.IsNullOrEmpty(pattern))
                throw new ArgumentException("Search 'pattern' must not be empty.", nameof(pattern));

            // Clamp tunables
            if (context < 0) context = 0;
            if (context > MaxContextLines) context = MaxContextLines;
            if (maxResults <= 0) maxResults = DefaultMaxResults;
            if (maxResults > HardResultsCap) maxResults = HardResultsCap;
            if (maxFileBytes <= 0) maxFileBytes = DefaultMaxFileBytes;

            // Default root + path-scope guard
            var rootRel = string.IsNullOrWhiteSpace(root) ? "Assets/" : root!.Trim();
            rootRel = rootRel.Replace('\\', '/').TrimStart('/');
            // Strip trailing slash for uniform handling, but keep at least one segment.
            var rootForCheck = rootRel.TrimEnd('/');
            if (rootForCheck.Length == 0)
                throw new ArgumentException("'root' must not be empty.", nameof(root));

            // Must start with 'Assets' or 'Packages' segment to prevent scanning outside the project tree.
            var firstSeg = rootForCheck.Split('/')[0];
            if (!string.Equals(firstSeg, "Assets", StringComparison.Ordinal) &&
                !string.Equals(firstSeg, "Packages", StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "'root' must be under 'Assets/' or 'Packages/'. Got: '" + rootRel + "'.",
                    nameof(root));
            }

            // Reject path traversal / absolute hops
            if (rootRel.Contains("..") || Path.IsPathRooted(rootRel))
                throw new ArgumentException("'root' must be a relative path under 'Assets/' or 'Packages/'.", nameof(root));

            var rootAbs = Path.GetFullPath(rootForCheck);
            if (!Directory.Exists(rootAbs))
                throw new ArgumentException("'root' directory not found: '" + rootRel + "'.", nameof(root));

            var globPattern = string.IsNullOrWhiteSpace(glob) ? "*.cs" : glob!.Trim();

            // ---- Regex compilation ---------------------------------------------------
            var effectivePattern = plainText ? Regex.Escape(pattern) : pattern;
            var options = RegexOptions.Compiled | RegexOptions.CultureInvariant;
            if (ignoreCase)
                options |= RegexOptions.IgnoreCase;

            Regex regex;
            try
            {
                regex = new Regex(effectivePattern, options, RegexTimeout);
            }
            catch (ArgumentException ex)
            {
                throw new ArgumentException("Invalid regex pattern: " + ex.Message, nameof(pattern), ex);
            }

            // Compile glob to a regex for path matching.
            Regex globRegex;
            try
            {
                globRegex = CompileGlob(globPattern);
            }
            catch (ArgumentException ex)
            {
                throw new ArgumentException("Invalid 'glob' pattern: " + ex.Message, nameof(glob), ex);
            }

            // ---- Scan ----------------------------------------------------------------
            var result = new FindResult();
            var matches = new List<FindMatch>();

            // Enumerate files lazily so we can early-exit when truncated.
            // We walk the tree manually so we can prune hidden directories.
            ScanDirectory(rootAbs, rootRel, globRegex, regex, context, maxResults, maxFileBytes, matches, result);

            result.Matches = matches.ToArray();
            result.TotalMatches = matches.Count;
            return result;
        }

        // -------------------------------------------------------------------------
        // Scanning
        // -------------------------------------------------------------------------

        static void ScanDirectory(
            string dirAbs,
            string dirRel,
            Regex globRegex,
            Regex contentRegex,
            int context,
            int maxResults,
            int maxFileBytes,
            List<FindMatch> matches,
            FindResult result)
        {
            if (result.Truncated)
                return;

            string[] entries;
            try
            {
                entries = Directory.GetFileSystemEntries(dirAbs);
            }
            catch (Exception)
            {
                // Unreadable directory — count nothing, continue.
                return;
            }

            // Files first (deterministic-ish), then sub-dirs.
            Array.Sort(entries, StringComparer.OrdinalIgnoreCase);

            foreach (var entry in entries)
            {
                if (result.Truncated)
                    return;

                bool isDir;
                try
                {
                    isDir = (File.GetAttributes(entry) & FileAttributes.Directory) == FileAttributes.Directory;
                }
                catch (Exception)
                {
                    continue;
                }

                var name = Path.GetFileName(entry);

                if (isDir)
                {
                    // Prune hidden dirs and known build artifacts.
                    if (name.Length > 0 && name[0] == '.')
                        continue;

                    var childRel = string.IsNullOrEmpty(dirRel) ? name : dirRel + "/" + name;
                    ScanDirectory(entry, childRel, globRegex, contentRegex, context, maxResults, maxFileBytes, matches, result);
                    continue;
                }

                // File — extension skip
                var ext = Path.GetExtension(name);
                if (!string.IsNullOrEmpty(ext) && BinarySkipExtensions.Contains(ext))
                    continue;

                var fileRel = string.IsNullOrEmpty(dirRel) ? name : dirRel + "/" + name;

                // Glob match against project-relative path.
                if (!globRegex.IsMatch(fileRel))
                    continue;

                // Size cap
                long size;
                try
                {
                    size = new FileInfo(entry).Length;
                }
                catch (Exception)
                {
                    result.FilesSkipped++;
                    continue;
                }
                if (size > maxFileBytes)
                {
                    result.FilesSkipped++;
                    continue;
                }

                result.FilesScanned++;

                var scanned = ScanFile(entry, fileRel, contentRegex, context, maxResults, matches);
                if (!scanned)
                    result.FilesSkipped++;

                if (matches.Count >= maxResults)
                {
                    result.Truncated = true;
                    return;
                }
            }
        }

        /// <summary>
        /// Streams the file line-by-line, applies <paramref name="regex"/>, and appends hits to <paramref name="matches"/>.
        /// Returns false if the file was unreadable or a regex-timeout aborted mid-scan.
        /// </summary>
        static bool ScanFile(
            string filePathAbs,
            string filePathRel,
            Regex regex,
            int context,
            int maxResults,
            List<FindMatch> matches)
        {
            // Ring buffer for "before" context, size = context.
            var before = context > 0 ? new Queue<string>(context + 1) : null;

            // When a match needs "after" context, we track it here and fill from subsequent lines.
            // Each entry: (matchIndexInMatchesList, remainingLinesToCapture)
            List<(int matchIdx, List<string> buffer, int needed)>? pendingAfter = context > 0 ? new List<(int, List<string>, int)>() : null;

            int lineNumber = 0;
            IEnumerable<string> linesEnumerable;
            try
            {
                linesEnumerable = File.ReadLines(filePathAbs);
            }
            catch (Exception)
            {
                return false;
            }

            try
            {
                foreach (var line in linesEnumerable)
                {
                    lineNumber++;

                    // 1) Fill "after" context for any pending matches first.
                    if (pendingAfter != null && pendingAfter.Count > 0)
                    {
                        for (int i = pendingAfter.Count - 1; i >= 0; i--)
                        {
                            var pa = pendingAfter[i];
                            pa.buffer.Add(line);
                            pa.needed--;
                            if (pa.needed <= 0)
                            {
                                matches[pa.matchIdx].ContextAfter = pa.buffer.ToArray();
                                pendingAfter.RemoveAt(i);
                            }
                            else
                            {
                                pendingAfter[i] = pa;
                            }
                        }
                    }

                    // 2) Run regex on this line.
                    MatchCollection lineMatches;
                    try
                    {
                        lineMatches = regex.Matches(line);
                    }
                    catch (RegexMatchTimeoutException)
                    {
                        // Skip this file — count it as a skip.
                        return false;
                    }

                    if (lineMatches.Count > 0)
                    {
                        // Snapshot of "before" context (cloned, since the queue keeps mutating).
                        string[] beforeArr = before != null && before.Count > 0
                            ? before.ToArray()
                            : Array.Empty<string>();

                        foreach (Match m in lineMatches)
                        {
                            if (!m.Success) continue;

                            var fm = new FindMatch
                            {
                                Path = filePathRel,
                                Line = lineNumber,
                                Column = m.Index + 1, // 1-based
                                MatchText = m.Value,
                                ContextBefore = beforeArr,
                                ContextAfter = Array.Empty<string>(),
                            };
                            matches.Add(fm);

                            // Queue "after" capture if requested.
                            if (context > 0 && pendingAfter != null)
                                pendingAfter.Add((matches.Count - 1, new List<string>(context), context));

                            if (matches.Count >= maxResults)
                            {
                                // Drain any pending-after for already-captured matches — best effort:
                                // they keep whatever they got. Stop scanning this file.
                                return true;
                            }
                        }
                    }

                    // 3) Update "before" ring buffer with this line for subsequent matches.
                    if (before != null)
                    {
                        if (before.Count == context)
                            before.Dequeue();
                        before.Enqueue(line);
                    }
                }
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }

            // Any still-pending "after" buffers (matches near EOF) get whatever was collected.
            if (pendingAfter != null)
            {
                foreach (var pa in pendingAfter)
                    matches[pa.matchIdx].ContextAfter = pa.buffer.ToArray();
            }

            return true;
        }

        // -------------------------------------------------------------------------
        // Glob compilation
        // -------------------------------------------------------------------------

        /// <summary>
        /// Converts a shell-style glob (supporting <c>*</c>, <c>**</c>, <c>?</c>) into a <see cref="Regex"/>
        /// that matches a project-relative path with forward-slash separators.
        /// A leading <c>**/</c> is implied — i.e. a glob like <c>*.cs</c> matches any depth.
        /// </summary>
        static Regex CompileGlob(string glob)
        {
            // Normalize separators
            glob = glob.Replace('\\', '/');

            var sb = new System.Text.StringBuilder();
            sb.Append('^');

            // If the glob does not start with '/', '**', or contain '/', allow any directory prefix.
            // i.e. "*.cs" should match "Foo/Bar/Baz.cs".
            bool startsWithDoubleStar = glob.StartsWith("**", StringComparison.Ordinal);
            bool containsSlash = glob.IndexOf('/') >= 0;
            if (!startsWithDoubleStar && !containsSlash)
                sb.Append("(?:.*/)?");

            int i = 0;
            while (i < glob.Length)
            {
                char c = glob[i];
                if (c == '*')
                {
                    bool isDouble = (i + 1 < glob.Length) && glob[i + 1] == '*';
                    if (isDouble)
                    {
                        // '**' matches across path separators
                        // Consume optional trailing '/'
                        i += 2;
                        if (i < glob.Length && glob[i] == '/')
                        {
                            // '**/' → zero or more path segments
                            sb.Append("(?:.*/)?");
                            i++;
                        }
                        else
                        {
                            sb.Append(".*");
                        }
                    }
                    else
                    {
                        // single '*' — match within a path segment (no '/')
                        sb.Append("[^/]*");
                        i++;
                    }
                }
                else if (c == '?')
                {
                    sb.Append("[^/]");
                    i++;
                }
                else if (c == '.' || c == '(' || c == ')' || c == '+' || c == '|' || c == '^' ||
                         c == '$' || c == '{' || c == '}' || c == '[' || c == ']' || c == '\\')
                {
                    // Regex metacharacter → escape
                    sb.Append('\\').Append(c);
                    i++;
                }
                else
                {
                    sb.Append(c);
                    i++;
                }
            }

            sb.Append('$');

            // Case-sensitive on Linux, case-insensitive on Windows/macOS to match filesystem semantics.
            var options = RegexOptions.Compiled | RegexOptions.CultureInvariant;
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                options |= RegexOptions.IgnoreCase;

            return new Regex(sb.ToString(), options, TimeSpan.FromMilliseconds(100));
        }
    }

    // ---------------------------------------------------------------------------
    // Result types
    // ---------------------------------------------------------------------------

    public class FindResult
    {
        public int FilesScanned { get; set; }
        public int FilesSkipped { get; set; }
        public int TotalMatches { get; set; }
        public bool Truncated { get; set; }  // true when maxResults reached
        public FindMatch[] Matches { get; set; } = System.Array.Empty<FindMatch>();
    }

    public class FindMatch
    {
        public string Path { get; set; } = string.Empty;  // project-relative, '/' separator
        public int Line { get; set; }       // 1-based
        public int Column { get; set; }     // 1-based
        public string MatchText { get; set; } = string.Empty;
        public string[] ContextBefore { get; set; } = System.Array.Empty<string>();
        public string[] ContextAfter { get; set; } = System.Array.Empty<string>();
    }
}
