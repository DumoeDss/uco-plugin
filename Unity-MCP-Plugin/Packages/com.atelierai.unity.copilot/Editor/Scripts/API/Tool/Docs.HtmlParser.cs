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
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    public partial class Tool_Docs
    {
        /// <summary>
        /// Lightweight Unity-doc HTML parser. Uses a streaming Regex-driven tokenizer
        /// (no external dependency) modeled after the original Python state machine
        /// in CoplayDev/unity-mcp.
        ///
        /// Two extraction modes:
        ///   - <see cref="ParseScriptReference"/> for ScriptReference pages: Description,
        ///     Declaration signatures, Parameters table, Returns, code examples.
        ///   - <see cref="ParseManualPage"/> for Manual / Package pages: h1 title, sections
        ///     keyed by h2/h3, and code-example pre blocks.
        /// </summary>
        internal static class HtmlParser
        {
            // -----------------------------------------------------------------
            // Public entry points
            // -----------------------------------------------------------------

            public static ScriptRefDocParsed ParseScriptReference(string html)
            {
                var p = new ScriptRefState();
                Walk(html, p);
                return new ScriptRefDocParsed
                {
                    Description = p.Description,
                    Signatures = p.Signatures.ToArray(),
                    Parameters = p.Parameters.ToArray(),
                    Returns = p.Returns,
                    Examples = p.Examples.ToArray(),
                };
            }

            public static ManualPageParsed ParseManualPage(string html)
            {
                var p = new ManualState();
                Walk(html, p);
                p.Flush();
                return new ManualPageParsed
                {
                    Title = p.Title,
                    Sections = p.Sections.ToArray(),
                    CodeExamples = p.CodeExamples.ToArray(),
                };
            }

            public class ScriptRefDocParsed
            {
                public string Description { get; set; } = string.Empty;
                public string[] Signatures { get; set; } = Array.Empty<string>();
                public ParamDoc[] Parameters { get; set; } = Array.Empty<ParamDoc>();
                public string Returns { get; set; } = string.Empty;
                public string[] Examples { get; set; } = Array.Empty<string>();
            }

            public class ManualPageParsed
            {
                public string Title { get; set; } = string.Empty;
                public ManualSection[] Sections { get; set; } = Array.Empty<ManualSection>();
                public string[] CodeExamples { get; set; } = Array.Empty<string>();
            }

            // -----------------------------------------------------------------
            // Tokenizer
            // -----------------------------------------------------------------

            // Matches an opening tag, closing tag, self-closing tag or a text run.
            // Tag attributes are captured in group 2 (start tag) so we can read class= etc.
            private static readonly Regex s_tagRegex = new(
                @"<(/)?([a-zA-Z][a-zA-Z0-9]*)\b([^>]*)>",
                RegexOptions.Compiled | RegexOptions.Singleline);

            private static readonly Regex s_attrRegex = new(
                @"([a-zA-Z_:][-a-zA-Z0-9_:.]*)\s*=\s*(""([^""]*)""|'([^']*)'|([^\s>]+))",
                RegexOptions.Compiled);

            private interface IHandler
            {
                void StartTag(string tag, Dictionary<string, string> attrs);
                void EndTag(string tag);
                void Text(string data);
            }

            private static void Walk(string html, IHandler handler)
            {
                if (string.IsNullOrEmpty(html))
                    return;

                // Strip <script> / <style> blocks (their contents would otherwise
                // pollute the body text we collect).
                html = StripBlock(html, "script");
                html = StripBlock(html, "style");

                int cursor = 0;
                foreach (Match m in s_tagRegex.Matches(html))
                {
                    if (m.Index > cursor)
                    {
                        var text = WebUtility.HtmlDecode(html.Substring(cursor, m.Index - cursor));
                        if (!string.IsNullOrEmpty(text))
                            handler.Text(text);
                    }
                    cursor = m.Index + m.Length;

                    var tag = m.Groups[2].Value.ToLowerInvariant();
                    var isClose = m.Groups[1].Value == "/";
                    if (isClose)
                    {
                        handler.EndTag(tag);
                    }
                    else
                    {
                        var attrs = ParseAttrs(m.Groups[3].Value);
                        handler.StartTag(tag, attrs);
                        // Treat void/self-closing tags as immediately closed so state machines stay clean.
                        if (m.Groups[3].Value.EndsWith("/", StringComparison.Ordinal) || IsVoidTag(tag))
                            handler.EndTag(tag);
                    }
                }
                if (cursor < html.Length)
                {
                    var tail = WebUtility.HtmlDecode(html.Substring(cursor));
                    if (!string.IsNullOrEmpty(tail))
                        handler.Text(tail);
                }
            }

            private static string StripBlock(string html, string tagName)
            {
                var pattern = $@"<{tagName}\b[^>]*>.*?</{tagName}>";
                return Regex.Replace(html, pattern, string.Empty, RegexOptions.Singleline | RegexOptions.IgnoreCase);
            }

            private static bool IsVoidTag(string tag) => tag switch
            {
                "br" or "hr" or "img" or "input" or "meta" or "link"
                    or "area" or "base" or "col" or "embed" or "param"
                    or "source" or "track" or "wbr" => true,
                _ => false,
            };

            private static Dictionary<string, string> ParseAttrs(string attrText)
            {
                var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (string.IsNullOrWhiteSpace(attrText))
                    return dict;
                foreach (Match a in s_attrRegex.Matches(attrText))
                {
                    var name = a.Groups[1].Value;
                    var value = a.Groups[3].Success ? a.Groups[3].Value
                              : a.Groups[4].Success ? a.Groups[4].Value
                              : a.Groups[5].Success ? a.Groups[5].Value
                              : string.Empty;
                    dict[name] = WebUtility.HtmlDecode(value);
                }
                return dict;
            }

            // -----------------------------------------------------------------
            // ScriptReference state machine
            // -----------------------------------------------------------------

            private sealed class ScriptRefState : IHandler
            {
                public string Description = string.Empty;
                public string Returns = string.Empty;
                public readonly List<string> Signatures = new();
                public readonly List<ParamDoc> Parameters = new();
                public readonly List<string> Examples = new();

                private bool _inSubsection;
                private int _subsectionDepth;
                private string? _subsectionTitle;
                private bool _inSignature;
                private bool _inPre;
                private bool _signatureFromPre;
                private bool _inCodeExample;
                private bool _inParamTable;
                private bool _inTd;
                private string? _tdClass;
                private bool _inHeading; // h2 or h3 inside subsection
                private bool _inParagraph;
                private Dictionary<string, string> _currentParam = new();
                private readonly StringBuilder _buf = new();

                public void StartTag(string tag, Dictionary<string, string> attrs)
                {
                    var classes = (attrs.TryGetValue("class", out var c) ? c : string.Empty).Split(' ');

                    if (tag == "div" && Array.IndexOf(classes, "subsection") >= 0)
                    {
                        _inSubsection = true;
                        _subsectionDepth = 1;
                        _subsectionTitle = null;
                    }
                    else if (tag == "div" && _inSubsection)
                    {
                        _subsectionDepth++;
                    }

                    if (tag == "div" && (Array.IndexOf(classes, "signature") >= 0 || Array.IndexOf(classes, "signature-CS") >= 0))
                    {
                        _inSignature = true;
                        if (Array.IndexOf(classes, "signature-CS") >= 0)
                            ResetBuf();
                    }

                    if ((tag == "h2" || tag == "h3") && _inSubsection)
                    {
                        _inHeading = true;
                        ResetBuf();
                    }

                    if (tag == "pre")
                    {
                        if (Array.IndexOf(classes, "codeExampleCS") >= 0)
                        {
                            _inCodeExample = true;
                            ResetBuf();
                        }
                        else if (_inSignature)
                        {
                            _inPre = true;
                            ResetBuf();
                        }
                    }

                    if (tag == "p" && _inSubsection)
                    {
                        _inParagraph = true;
                        ResetBuf();
                    }

                    if (tag == "table" && _inSubsection)
                        _inParamTable = true;

                    if (tag == "td" && _inParamTable)
                    {
                        _inTd = true;
                        _tdClass = attrs.TryGetValue("class", out var tc) ? tc : string.Empty;
                        ResetBuf();
                    }

                    if (tag == "tr" && _inParamTable)
                        _currentParam = new Dictionary<string, string>();
                }

                public void EndTag(string tag)
                {
                    if ((tag == "h2" || tag == "h3") && _inHeading)
                    {
                        _inHeading = false;
                        _subsectionTitle = _buf.ToString().Trim();
                    }

                    if (tag == "pre")
                    {
                        if (_inCodeExample)
                        {
                            _inCodeExample = false;
                            Examples.Add(_buf.ToString().Trim());
                        }
                        else if (_inPre)
                        {
                            _inPre = false;
                            Signatures.Add(_buf.ToString().Trim());
                            _signatureFromPre = true;
                        }
                    }

                    if (tag == "div" && _inSignature)
                    {
                        if (!_signatureFromPre)
                        {
                            var text = CollapseWhitespace(_buf.ToString()).Trim();
                            if (text.StartsWith("Declaration", StringComparison.Ordinal))
                                text = text.Substring("Declaration".Length).Trim();
                            if (text.Length > 0)
                                Signatures.Add(text);
                        }
                        _inSignature = false;
                        _signatureFromPre = false;
                    }

                    if (tag == "p" && _inParagraph)
                    {
                        _inParagraph = false;
                        var text = _buf.ToString().Trim();
                        if (text.Length > 0 && string.Equals(_subsectionTitle, "Description", StringComparison.Ordinal) && Description.Length == 0)
                            Description = text;
                        else if (text.Length > 0 && string.Equals(_subsectionTitle, "Returns", StringComparison.Ordinal) && Returns.Length == 0)
                            Returns = text;
                    }

                    if (tag == "td" && _inTd)
                    {
                        _inTd = false;
                        var text = _buf.ToString().Trim();
                        if (!string.IsNullOrEmpty(_tdClass))
                        {
                            var split = _tdClass.Split(' ');
                            if (_tdClass.Contains("name-collumn") || Array.IndexOf(split, "name") >= 0)
                                _currentParam["name"] = text;
                            else if (_tdClass.Contains("desc-collumn") || Array.IndexOf(split, "desc") >= 0)
                                _currentParam["description"] = text;
                        }
                    }

                    if (tag == "tr" && _inParamTable)
                    {
                        if (_currentParam.TryGetValue("name", out var n) && !string.IsNullOrEmpty(n))
                        {
                            _currentParam.TryGetValue("description", out var d);
                            Parameters.Add(new ParamDoc { Name = n, Description = d ?? string.Empty });
                        }
                        _currentParam = new Dictionary<string, string>();
                    }

                    if (tag == "table" && _inParamTable)
                        _inParamTable = false;

                    if (tag == "div" && _inSubsection)
                    {
                        _subsectionDepth--;
                        if (_subsectionDepth <= 0)
                            _inSubsection = false;
                    }
                }

                public void Text(string data)
                {
                    if (_inHeading || _inPre || _inCodeExample || _inParagraph || _inTd || _inSignature)
                        _buf.Append(data);
                }

                private void ResetBuf() => _buf.Clear();
            }

            // -----------------------------------------------------------------
            // Manual / Package state machine
            // -----------------------------------------------------------------

            private sealed class ManualState : IHandler
            {
                public string Title = string.Empty;
                public readonly List<ManualSection> Sections = new();
                public readonly List<string> CodeExamples = new();

                private bool _inH1;
                private bool _inHeading;
                private bool _inP;
                private bool _inPre;
                private readonly StringBuilder _buf = new();
                private string? _currentHeading;
                private readonly List<string> _contentParts = new();

                public void StartTag(string tag, Dictionary<string, string> attrs)
                {
                    if (tag == "h1" && string.IsNullOrEmpty(Title))
                    {
                        _inH1 = true;
                        _buf.Clear();
                    }
                    else if (tag == "h2" || tag == "h3")
                    {
                        Flush();
                        _inHeading = true;
                        _buf.Clear();
                    }
                    else if (tag == "p")
                    {
                        _inP = true;
                        _buf.Clear();
                    }
                    else if (tag == "pre")
                    {
                        _inPre = true;
                        _buf.Clear();
                    }
                }

                public void EndTag(string tag)
                {
                    if (tag == "h1" && _inH1)
                    {
                        _inH1 = false;
                        Title = _buf.ToString().Trim();
                    }
                    else if ((tag == "h2" || tag == "h3") && _inHeading)
                    {
                        _inHeading = false;
                        _currentHeading = _buf.ToString().Trim();
                        _contentParts.Clear();
                    }
                    else if (tag == "p" && _inP)
                    {
                        _inP = false;
                        var text = _buf.ToString().Trim();
                        if (text.Length > 0)
                            _contentParts.Add(text);
                    }
                    else if (tag == "pre" && _inPre)
                    {
                        _inPre = false;
                        var code = _buf.ToString().Trim();
                        if (code.Length > 0)
                            CodeExamples.Add(code);
                    }
                }

                public void Text(string data)
                {
                    if (_inH1 || _inHeading || _inP || _inPre)
                        _buf.Append(data);
                }

                public void Flush()
                {
                    if (_contentParts.Count == 0 && _currentHeading is null)
                        return;
                    Sections.Add(new ManualSection
                    {
                        Heading = _currentHeading ?? "Introduction",
                        Content = string.Join("\n", _contentParts),
                    });
                    _currentHeading = null;
                    _contentParts.Clear();
                }
            }

            // -----------------------------------------------------------------
            // Helpers
            // -----------------------------------------------------------------

            private static readonly Regex s_wsCollapse = new(@"\s+", RegexOptions.Compiled);

            private static string CollapseWhitespace(string s)
                => s_wsCollapse.Replace(s, " ");

            // -----------------------------------------------------------------
            // Version extraction (shared by all docs-* tools)
            // -----------------------------------------------------------------

            private static readonly Regex s_versionTail = new(@"[a-zA-Z].*$", RegexOptions.Compiled);

            /// <summary>
            /// Extracts major.minor from a full Unity version string.
            /// "6000.0.38f1" -> "6000.0", "2022.3.45f1" -> "2022.3", null/"" -> null.
            /// </summary>
            public static string? ExtractVersion(string? versionStr)
            {
                if (string.IsNullOrWhiteSpace(versionStr))
                    return null;
                var parts = versionStr!.Split('.');
                if (parts.Length < 2)
                    return versionStr;
                var second = s_versionTail.Replace(parts[1], string.Empty);
                return $"{parts[0]}.{second}";
            }

            // -----------------------------------------------------------------
            // URL builders (shared)
            // -----------------------------------------------------------------

            /// <summary>ScriptReference URL using the dot separator for members.</summary>
            public static string BuildScriptRefUrl(string className, string? memberName, string? version)
            {
                var page = string.IsNullOrEmpty(memberName)
                    ? $"{className}.html"
                    : $"{className}.{memberName}.html";
                return string.IsNullOrEmpty(version)
                    ? $"https://docs.unity3d.com/ScriptReference/{page}"
                    : $"https://docs.unity3d.com/{version}/Documentation/ScriptReference/{page}";
            }

            /// <summary>ScriptReference URL using the dash separator (property style).</summary>
            public static string BuildPropertyUrl(string className, string memberName, string? version)
            {
                var page = $"{className}-{memberName}.html";
                return string.IsNullOrEmpty(version)
                    ? $"https://docs.unity3d.com/ScriptReference/{page}"
                    : $"https://docs.unity3d.com/{version}/Documentation/ScriptReference/{page}";
            }

            public static string BuildManualUrl(string slug, string? version)
                => string.IsNullOrEmpty(version)
                    ? $"https://docs.unity3d.com/Manual/{slug}.html"
                    : $"https://docs.unity3d.com/{version}/Documentation/Manual/{slug}.html";

            public static string BuildPackageUrl(string package, string page, string pkgVersion)
                => $"https://docs.unity3d.com/Packages/{package}@{pkgVersion}/manual/{page}.html";
        }
    }
}
