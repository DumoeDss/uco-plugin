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
        public const string DocsGetPackageToolId = "docs-get-package";

        [McpPluginTool
        (
            DocsGetPackageToolId,
            Title = "Docs / Get Package",
            ReadOnlyHint = true,
            IdempotentHint = true,
            OpenWorldHint = true
        )]
        [McpPluginSkillDescription("Fetch a Unity package documentation page " +
            "(https://docs.unity3d.com/Packages/<package>@<pkgVersion>/manual/<page>.html) and parse it into title + " +
            "sections + code examples. Use it for URP/HDRP/Input System/UI Toolkit and any other UPM package's " +
            "official manual.")]
        [McpPluginSkillBody("Fetches a Unity **Package** documentation page and returns the parsed structure.\n\n" +
            "## Inputs\n\n" +
            "- `package` *(required)* — package id such as `com.unity.render-pipelines.universal`.\n" +
            "- `page` *(required)* — page slug under the package's `manual/` folder, e.g. `index`, `2d-index`, " +
            "`whats-new`.\n" +
            "- `pkgVersion` *(required)* — package version such as `17.0` or `17.0.3`.\n\n" +
            "## Result\n\n" +
            "Returns `ManualPage` with `Found`, `Url`, `Title`, `Sections[]`, `CodeExamples[]`.")]
        [Description("Fetch a Unity package manual page and return its parsed title/sections/code examples.")]
        public async Task<ManualPage> GetPackage
        (
            [Description("Package id, e.g. 'com.unity.render-pipelines.universal'. Required.")]
            string package,
            [Description("Page slug under the package's manual folder, e.g. 'index', '2d-index', 'whats-new'. Required.")]
            string page,
            [Description("Package version, e.g. '17.0' or '17.0.3'. Required.")]
            string pkgVersion
        )
        {
            if (string.IsNullOrWhiteSpace(package) || string.IsNullOrWhiteSpace(page) || string.IsNullOrWhiteSpace(pkgVersion))
                throw new ArgumentException(Error.PackageArgsMissing());

            return await GetPackageInternal(package, page, pkgVersion).ConfigureAwait(false);
        }

        internal static async Task<ManualPage> GetPackageInternal(string package, string page, string pkgVersion)
        {
            var url = HtmlParser.BuildPackageUrl(package, page, pkgVersion);
            var result = await HttpFetcher.FetchAsync(url).ConfigureAwait(false);

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
                    Suggestion = "Package page not found. Verify the package id, version and page slug are all correct. Common pages: 'index', 'installation', 'whats-new'.",
                };
            }

            var parsed = HtmlParser.ParseManualPage(result.Body);
            return new ManualPage
            {
                Found = true,
                Url = result.FinalUrl,
                Title = string.IsNullOrEmpty(parsed.Title) ? null : parsed.Title,
                Sections = parsed.Sections.Length == 0 ? null : parsed.Sections,
                CodeExamples = parsed.CodeExamples.Length == 0 ? null : parsed.CodeExamples,
            };
        }
    }
}
