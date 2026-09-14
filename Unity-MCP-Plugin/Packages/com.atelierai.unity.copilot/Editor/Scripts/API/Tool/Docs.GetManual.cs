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
using System.ComponentModel;
using System.Threading.Tasks;
using com.IvanMurzak.McpPlugin;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    public partial class Tool_Docs
    {
        public const string DocsGetManualToolId = "docs-get-manual";

        [McpPluginTool
        (
            DocsGetManualToolId,
            Title = "Docs / Get Manual",
            ReadOnlyHint = true,
            IdempotentHint = true,
            OpenWorldHint = true
        )]
        [McpPluginSkillDescription("Fetch a Unity Manual page (https://docs.unity3d.com/Manual/<slug>.html) " +
            "and parse it into title + sections + code examples. Use it to look up architectural docs, workflow guides " +
            "and feature references that don't live in the ScriptReference.")]
        [McpPluginSkillBody("Fetches a Unity **Manual** page by slug and returns the parsed structure.\n\n" +
            "## Inputs\n\n" +
            "- `slug` *(required)* — the Manual page slug, e.g. `execution-order`, `urp/urp-introduction`, " +
            "`UIE-USS-Properties-Reference`. Case matches the URL.\n" +
            "- `version` *(optional)* — Unity version (e.g. `6000.0.38f1`); only `major.minor` is used.\n\n" +
            "## Fallback behavior\n\n" +
            "If the versioned URL 404s, the tool retries the unversioned (`/Manual/<slug>.html`) URL once.\n\n" +
            "## Result\n\n" +
            "Returns `ManualPage` with `Found`, `Url`, `Title`, `Sections[]` (heading + content) and `CodeExamples[]`.")]
        [Description("Fetch a Unity Manual page by slug and return its parsed title/sections/code examples.")]
        public async Task<ManualPage> GetManual
        (
            [Description("Manual page slug as it appears in the URL, e.g. 'execution-order', 'urp/urp-introduction'. Required.")]
            string slug,
            [Description("Full Unity version such as '6000.0.38f1'. Only the major.minor portion is used. Optional.")]
            string? version = null
        )
        {
            if (string.IsNullOrWhiteSpace(slug))
                throw new ArgumentException(Error.SlugIsEmpty());

            return await GetManualInternal(slug, version).ConfigureAwait(false);
        }

        internal static async Task<ManualPage> GetManualInternal(string slug, string? version)
        {
            var v = HtmlParser.ExtractVersion(version);
            var url = HtmlParser.BuildManualUrl(slug, v);

            var result = await HttpFetcher.FetchAsync(url).ConfigureAwait(false);

            // versioned 404 -> unversioned fallback
            if (result.IsNotFound && !string.IsNullOrEmpty(v))
            {
                var fbUrl = HtmlParser.BuildManualUrl(slug, null);
                var fb = await HttpFetcher.FetchAsync(fbUrl).ConfigureAwait(false);
                if (fb.IsOk)
                {
                    result = fb;
                    url = fbUrl;
                }
                else
                {
                    result = fb;
                }
            }

            if (result.IsNetworkError)
            {
                return new ManualPage
                {
                    Found = false,
                    Suggestion = result.ErrorMessage ?? "Could not reach docs.unity3d.com.",
                };
            }

            if (!result.IsOk)
            {
                return new ManualPage
                {
                    Found = false,
                    Suggestion = "Manual page not found. Check the slug matches the URL path. Common slugs: 'execution-order', 'urp/urp-introduction', 'UIE-USS-Properties-Reference'.",
                };
            }

            var parsed = HtmlParser.ParseManualPage(result.Body);
            return new ManualPage
            {
                Found = true,
                Url = url,
                Title = string.IsNullOrEmpty(parsed.Title) ? null : parsed.Title,
                Sections = parsed.Sections.Length == 0 ? null : parsed.Sections,
                CodeExamples = parsed.CodeExamples.Length == 0 ? null : parsed.CodeExamples,
            };
        }
    }
}
