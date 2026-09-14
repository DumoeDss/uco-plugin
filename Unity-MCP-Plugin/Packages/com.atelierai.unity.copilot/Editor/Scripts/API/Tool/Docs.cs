/*
 * Portions of this file are derived from MCP for Unity (CoplayDev/unity-mcp),
 * Copyright (c) Coplay Inc., licensed under the MIT License.
 * Original: https://github.com/CoplayDev/unity-mcp/blob/main/Server/src/services/tools/unity_docs.py
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
using com.AtelierAI.Uco.Framework;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    [UcoToolType]
    public partial class Tool_Docs
    {
        public static class Error
        {
            public static string ClassNameIsEmpty()
                => "className is empty. Provide a Unity class name such as 'Physics' or 'Transform'.";

            public static string SlugIsEmpty()
                => "slug is empty. Provide a Unity Manual page slug such as 'execution-order' or 'urp/urp-introduction'.";

            public static string PackageArgsMissing()
                => "package, page and pkgVersion are all required. Example: package='com.unity.render-pipelines.universal', page='2d-index', pkgVersion='17.0'.";

            public static string QueriesEmpty()
                => "queries is empty. Provide at least one query string.";
        }

        /// <summary>
        /// Structured result of a Unity ScriptReference doc fetch.
        /// </summary>
        [Description("Unity ScriptReference documentation result. When 'found' is false, other fields are null and 'suggestion' explains the miss.")]
        public class ScriptRefDoc
        {
            [Description("True when the page was fetched and parsed successfully.")]
            public bool Found { get; set; }

            [Description("Canonical URL the data was fetched from (after any fallbacks).")]
            public string? Url { get; set; }

            [Description("Class name that was requested.")]
            public string ClassName { get; set; } = string.Empty;

            [Description("Member (method/property) name that was requested, or null for class-level docs.")]
            public string? MemberName { get; set; }

            [Description("Plain-text description block from the page.")]
            public string? Description { get; set; }

            [Description("All C# signature lines from the Declaration block.")]
            public string[]? Signatures { get; set; }

            [Description("Parameter descriptions parsed from the Parameters table.")]
            public ParamDoc[]? Parameters { get; set; }

            [Description("Plain-text Returns block, if present.")]
            public string? Returns { get; set; }

            [Description("C# code examples from the page.")]
            public string[]? Examples { get; set; }

            [Description("Hint string when 'found' is false (network error, 404, etc.).")]
            public string? Suggestion { get; set; }
        }

        [Description("A single parameter row from a Unity doc page.")]
        public class ParamDoc
        {
            [Description("Parameter name as documented.")]
            public string Name { get; set; } = string.Empty;

            [Description("Parameter description text.")]
            public string Description { get; set; } = string.Empty;
        }

        /// <summary>
        /// Structured result of a Unity Manual or Package documentation page fetch.
        /// </summary>
        [Description("Unity Manual or Package documentation result. When 'found' is false, other fields are null and 'suggestion' explains the miss.")]
        public class ManualPage
        {
            [Description("True when the page was fetched and parsed successfully.")]
            public bool Found { get; set; }

            [Description("Canonical URL the data was fetched from (after any fallbacks).")]
            public string? Url { get; set; }

            [Description("Page title (h1 element).")]
            public string? Title { get; set; }

            [Description("Section list, where each section has a heading and a content text block.")]
            public ManualSection[]? Sections { get; set; }

            [Description("Code blocks (<pre>) extracted from the page.")]
            public string[]? CodeExamples { get; set; }

            [Description("Hint string when 'found' is false (network error, 404, etc.).")]
            public string? Suggestion { get; set; }
        }

        [Description("A single section parsed out of a Unity Manual or Package documentation page.")]
        public class ManualSection
        {
            [Description("Section heading text (h2/h3).")]
            public string Heading { get; set; } = string.Empty;

            [Description("Concatenated paragraph text for the section.")]
            public string Content { get; set; } = string.Empty;
        }

        /// <summary>
        /// Aggregated result of a docs-lookup call across ScriptReference / Manual / Package docs.
        /// </summary>
        [Description("Aggregated lookup result. Contains zero or more hits across ScriptReference, Manual and Package documentation sources.")]
        public class LookupResult
        {
            [Description("Echo of the input queries.")]
            public string[] Queries { get; set; } = System.Array.Empty<string>();

            [Description("Successful hits. Each carries the source label (script_ref/manual/package) and a payload.")]
            public LookupHit[] Hits { get; set; } = System.Array.Empty<LookupHit>();

            [Description("Number of queries that returned at least one hit.")]
            public int TotalFound { get; set; }

            [Description("Number of queries that produced zero hits.")]
            public int TotalMissed { get; set; }

            [Description("Hint shown when there are misses, explaining how to refine the search.")]
            public string? Suggestion { get; set; }
        }

        [Description("One successful hit in a LookupResult.")]
        public class LookupHit
        {
            [Description("Source label: 'script_ref', 'manual' or 'package'.")]
            public string Source { get; set; } = string.Empty;

            [Description("The query that produced this hit.")]
            public string Query { get; set; } = string.Empty;

            [Description("Source-specific payload. For 'script_ref' this is a ScriptRefDoc; for 'manual' and 'package' a ManualPage.")]
            public object? Data { get; set; }
        }
    }
}
