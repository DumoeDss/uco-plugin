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
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using com.IvanMurzak.McpPlugin;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    public partial class Tool_Docs
    {
        public const string DocsLookupToolId = "docs-lookup";

        [McpPluginTool
        (
            DocsLookupToolId,
            Title = "Docs / Lookup",
            ReadOnlyHint = true,
            IdempotentHint = true,
            OpenWorldHint = true
        )]
        [McpPluginSkillDescription("Look up a batch of Unity documentation queries in parallel across ScriptReference, " +
            "Manual and (optionally) Package docs. Each query is dispatched to all configured sources concurrently and " +
            "every successful hit is returned. Use it as the broad 'I don't know where this lives' entry point.")]
        [McpPluginSkillBody("Searches Unity docs across multiple sources in parallel.\n\n" +
            "## Inputs\n\n" +
            "- `queries` *(required)* — one or more query strings. Each entry is split on '.' into class + member " +
            "for the ScriptReference probe, and slugified ('-' join, lowercase fallback) for the Manual probe.\n" +
            "- `version` *(optional)* — Unity version (only `major.minor` is used).\n" +
            "- `package` + `pkgVersion` *(optional)* — when both provided, package docs are also probed.\n\n" +
            "## Behavior\n\n" +
            "All queries × all sources run via `Task.WhenAll`. A failing source never blocks a successful one. " +
            "Hits are returned in order: ScriptReference, Manual, Package.\n\n" +
            "## Result\n\n" +
            "Returns a `LookupResult` with `Queries[]`, `Hits[]` (each `LookupHit` has `Source`, `Query`, `Data`), " +
            "`TotalFound`, `TotalMissed`, and a `Suggestion` when there are misses.")]
        [Description("Look up one or more Unity doc queries across ScriptReference, Manual, and Package docs in parallel.")]
        public async Task<LookupResult> Lookup
        (
            [Description("One or more query strings. Examples: 'Physics.Raycast', 'NavMeshAgent', 'execution-order'. Required.")]
            string[] queries,
            [Description("Full Unity version such as '6000.0.38f1'. Only the major.minor portion is used. Optional.")]
            string? version = null,
            [Description("Optional package id (e.g. 'com.unity.render-pipelines.universal') to also probe package docs. Requires pkgVersion.")]
            string? package = null,
            [Description("Optional package version (e.g. '17.0'). Requires package.")]
            string? pkgVersion = null
        )
        {
            if (queries == null || queries.Length == 0 || queries.All(string.IsNullOrWhiteSpace))
                throw new ArgumentException(Error.QueriesEmpty());

            var filteredQueries = queries.Where(q => !string.IsNullOrWhiteSpace(q)).Select(q => q.Trim()).ToArray();

            // Dispatch one task per query; each query internally fans out to multiple sources.
            var perQueryTasks = filteredQueries
                .Select(q => LookupSingleAsync(q, version, package, pkgVersion))
                .ToArray();

            var perQueryResults = await Task.WhenAll(perQueryTasks).ConfigureAwait(false);

            var allHits = new List<LookupHit>();
            int found = 0;
            int missed = 0;

            foreach (var hits in perQueryResults)
            {
                if (hits.Count > 0)
                {
                    found++;
                    allHits.AddRange(hits);
                }
                else
                {
                    missed++;
                }
            }

            return new LookupResult
            {
                Queries = filteredQueries,
                Hits = allHits.ToArray(),
                TotalFound = found,
                TotalMissed = missed,
                Suggestion = missed == 0
                    ? null
                    : "Some queries had zero hits. Try: (1) 'docs-get-script-reference' with the exact class/member name; "
                        + "(2) 'docs-get-manual' with the precise slug; (3) 'reflection-method-find' to verify the C# API exists; "
                        + "(4) provide a `package` + `pkgVersion` if the topic lives in a UPM package's manual.",
            };
        }

        /// <summary>
        /// Probe every source for a single query in parallel. Returns the (possibly empty)
        /// ordered list of hits for that query. Order: script_ref, manual, package.
        /// </summary>
        private static async Task<List<LookupHit>> LookupSingleAsync(
            string query,
            string? version,
            string? package,
            string? pkgVersion)
        {
            // ScriptReference probe — split class.member, but not when the query starts
            // with "com." (which is a UPM package id, not a class).
            string className = query;
            string? memberName = null;
            if (query.Contains('.') && !query.StartsWith("com.", StringComparison.OrdinalIgnoreCase))
            {
                var idx = query.LastIndexOf('.');
                className = query.Substring(0, idx);
                memberName = query.Substring(idx + 1);
            }

            // Manual probe — slugify by replacing whitespace/underscore with '-'.
            var originalSlug = query.Replace(' ', '-').Replace('_', '-');
            var lowercaseSlug = originalSlug.ToLowerInvariant();

            // Build parallel tasks.
            var tasks = new List<(string source, Task<object?> task)>(6)
            {
                ("script_ref", AsObject(GetScriptReferenceInternal(className, memberName, version))),
                ("manual", AsObject(GetManualInternal(originalSlug, version))),
            };
            if (!string.Equals(lowercaseSlug, originalSlug, StringComparison.Ordinal))
                tasks.Add(("manual", AsObject(GetManualInternal(lowercaseSlug, version))));

            if (!string.IsNullOrWhiteSpace(package) && !string.IsNullOrWhiteSpace(pkgVersion))
            {
                tasks.Add(("package", AsObject(GetPackageInternal(package!, originalSlug, pkgVersion!))));
                if (!string.Equals(lowercaseSlug, originalSlug, StringComparison.Ordinal))
                    tasks.Add(("package", AsObject(GetPackageInternal(package!, lowercaseSlug, pkgVersion!))));
            }

            try
            {
                await Task.WhenAll(tasks.Select(t => t.task)).ConfigureAwait(false);
            }
            catch
            {
                // Individual exceptions are inspected per-task below; swallow the aggregate.
            }

            // Collect successful hits, preserving source priority (script_ref > manual > package).
            var hits = new List<LookupHit>();
            // Dedupe Manual URLs that we may have probed in both case variants.
            var seenUrls = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (source, task) in tasks)
            {
                if (task.IsFaulted || task.IsCanceled)
                    continue;
                var data = task.Result;
                if (!IsFound(data, out var url))
                    continue;
                if (!string.IsNullOrEmpty(url) && !seenUrls.Add(url!))
                    continue;
                hits.Add(new LookupHit { Source = source, Query = query, Data = data });
            }

            // Sort: script_ref (0), manual (1), package (2). Keeps each query's hits in priority order.
            hits.Sort((a, b) => SourceRank(a.Source).CompareTo(SourceRank(b.Source)));
            return hits;
        }

        private static int SourceRank(string source) => source switch
        {
            "script_ref" => 0,
            "manual" => 1,
            "package" => 2,
            _ => 99,
        };

        private static bool IsFound(object? data, out string? url)
        {
            url = null;
            switch (data)
            {
                case ScriptRefDoc s when s.Found:
                    url = s.Url;
                    return true;
                case ManualPage m when m.Found:
                    url = m.Url;
                    return true;
                default:
                    return false;
            }
        }

        // Helpers to unify return types for Task.WhenAll.
        private static async Task<object?> AsObject(Task<ScriptRefDoc> t) => await t.ConfigureAwait(false);
        private static async Task<object?> AsObject(Task<ManualPage> t) => await t.ConfigureAwait(false);
    }
}
