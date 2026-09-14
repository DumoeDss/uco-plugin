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
using com.AtelierAI.Uco.Framework;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    public partial class Tool_Docs
    {
        public const string DocsGetScriptReferenceToolId = "docs-get-script-reference";

        [UcoTool
        (
            DocsGetScriptReferenceToolId,
            Title = "Docs / Get ScriptReference",
            ReadOnlyHint = true,
            IdempotentHint = true,
            OpenWorldHint = true
        )]
        [UcoSkillDescription("Fetch a Unity ScriptReference page (https://docs.unity3d.com/ScriptReference/) " +
            "and parse the Description, Declaration signatures, Parameters table, Returns block and code examples " +
            "into a structured `ScriptRefDoc` payload. Use it to verify class / method signatures and find usage examples " +
            "before writing C# code.")]
        [UcoSkillBody("Fetches an official Unity **ScriptReference** page and returns its structured contents.\n\n" +
            "## Inputs\n\n" +
            "- `className` *(required)* — Unity class such as `Physics`, `Transform`, `Rigidbody`.\n" +
            "- `memberName` *(optional)* — method or property name to look up. If omitted, returns the class page.\n" +
            "- `version` *(optional)* — Unity version (e.g. `6000.0.38f1`); the tool extracts `major.minor` (`6000.0`).\n\n" +
            "## Fallback behavior\n\n" +
            "If the versioned + method-style URL 404s, the tool retries with:\n" +
            "1. The property-style URL (`Class-member.html`).\n" +
            "2. The unversioned URL.\n" +
            "3. The unversioned property-style URL.\n\n" +
            "## Result\n\n" +
            "Returns `ScriptRefDoc` with `Found`, `Url`, `Description`, `Signatures[]`, `Parameters[]`, `Returns`, `Examples[]`. " +
            "When the page cannot be reached, `Found = false` and `Suggestion` explains the miss.")]
        [Description("Fetch a Unity ScriptReference page (class or member) and return a structured ScriptRefDoc. " +
            "Verifies class / method signatures and surfaces official code examples for self-correction.")]
        public async Task<ScriptRefDoc> GetScriptReference
        (
            [Description("Unity class name, e.g. 'Physics', 'Transform', 'AnimationCurve'. Required.")]
            string className,
            [Description("Method or property name on the class, e.g. 'Raycast', 'position'. Optional — omit for the class-level page.")]
            string? memberName = null,
            [Description("Full Unity version such as '6000.0.38f1'. Only the major.minor portion is used. Optional — omitted means latest.")]
            string? version = null
        )
        {
            if (string.IsNullOrWhiteSpace(className))
                throw new ArgumentException(Error.ClassNameIsEmpty());

            return await GetScriptReferenceInternal(className, memberName, version).ConfigureAwait(false);
        }

        // Internal helper, reused by docs-lookup.
        internal static async Task<ScriptRefDoc> GetScriptReferenceInternal(string className, string? memberName, string? version)
        {
            var v = HtmlParser.ExtractVersion(version);
            var url = HtmlParser.BuildScriptRefUrl(className, memberName, v);

            var result = await HttpFetcher.FetchAsync(url).ConfigureAwait(false);

            // 1) method-style 404 -> property-style URL
            if (result.IsNotFound && !string.IsNullOrEmpty(memberName))
            {
                var propUrl = HtmlParser.BuildPropertyUrl(className, memberName!, v);
                var propResult = await HttpFetcher.FetchAsync(propUrl).ConfigureAwait(false);
                if (propResult.IsOk)
                {
                    result = propResult;
                    url = propUrl;
                }
                else if (propResult.IsNotFound)
                {
                    result = propResult; // keep 404 status for further fallback
                }
            }

            // 2) versioned 404 -> unversioned URL
            if (result.IsNotFound && !string.IsNullOrEmpty(v))
            {
                var fbUrl = HtmlParser.BuildScriptRefUrl(className, memberName, null);
                var fb = await HttpFetcher.FetchAsync(fbUrl).ConfigureAwait(false);
                if (fb.IsOk)
                {
                    result = fb;
                    url = fbUrl;
                }
                else if (!string.IsNullOrEmpty(memberName))
                {
                    var propFbUrl = HtmlParser.BuildPropertyUrl(className, memberName!, null);
                    var propFb = await HttpFetcher.FetchAsync(propFbUrl).ConfigureAwait(false);
                    if (propFb.IsOk)
                    {
                        result = propFb;
                        url = propFbUrl;
                    }
                    else
                    {
                        result = fb;
                    }
                }
                else
                {
                    result = fb;
                }
            }

            if (result.IsNetworkError)
            {
                return new ScriptRefDoc
                {
                    Found = false,
                    ClassName = className,
                    MemberName = memberName,
                    Suggestion = result.ErrorMessage ?? "Could not reach docs.unity3d.com.",
                };
            }

            if (!result.IsOk)
            {
                return new ScriptRefDoc
                {
                    Found = false,
                    ClassName = className,
                    MemberName = memberName,
                    Suggestion = "Page not found. Verify the class/member name (case-sensitive) — try 'docs-lookup' or 'gameobject-component-list-all'/'reflection-method-find' first to confirm the type exists.",
                };
            }

            var parsed = HtmlParser.ParseScriptReference(result.Body);
            return new ScriptRefDoc
            {
                Found = true,
                Url = url,
                ClassName = className,
                MemberName = memberName,
                Description = string.IsNullOrEmpty(parsed.Description) ? null : parsed.Description,
                Signatures = parsed.Signatures.Length == 0 ? null : parsed.Signatures,
                Parameters = parsed.Parameters.Length == 0 ? null : parsed.Parameters,
                Returns = string.IsNullOrEmpty(parsed.Returns) ? null : parsed.Returns,
                Examples = parsed.Examples.Length == 0 ? null : parsed.Examples,
            };
        }
    }
}
