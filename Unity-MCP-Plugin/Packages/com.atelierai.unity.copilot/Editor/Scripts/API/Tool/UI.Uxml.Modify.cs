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
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using System.Xml.XPath;
using com.IvanMurzak.McpPlugin;
using com.IvanMurzak.ReflectorNet.Utils;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    public partial class Tool_UI
    {
        public const string UxmlModifyToolId = "ui-uxml-modify";

        [McpPluginTool
        (
            UxmlModifyToolId,
            Title = "UI / UXML / Modify",
            DestructiveHint = true
        )]
        [McpPluginSkillDescription("Structurally modify a UXML file: replace an element, append a child element, " +
            "remove an element, or set an attribute. Targets are selected via a small XPath subset that knows about " +
            "the 'ui:' (UnityEngine.UIElements) and 'uie:' (UnityEditor.UIElements) namespace prefixes. " +
            "Pair with '" + UxmlReadToolId + "' to inspect existing content before modifying.")]
        [McpPluginSkillBody("Structurally modify a UXML asset.\n\n" +
            "## Operations\n\n" +
            "- `replace` — replace the matched element with the XML fragment in `newContent`.\n" +
            "- `append-child` — append the XML fragment in `newContent` as a new last child of the matched element.\n" +
            "- `remove-element` — delete the matched element from its parent. Cannot be used on the document root.\n" +
            "- `set-attribute` — set `attributeName=attributeValue` on the matched element (pass empty string to clear).\n\n" +
            "## Selector syntax\n\n" +
            "An XPath-like expression resolved with `XPathSelectElements`. Use the prefix `ui:` for Unity's main " +
            "UI Toolkit namespace and `uie:` for the editor namespace — both prefixes are registered automatically " +
            "before the XPath query runs. Examples:\n\n" +
            "- `//ui:Label[@name='title']` — every Label with name='title'.\n" +
            "- `/ui:UXML/ui:VisualElement` — direct children VisualElements of the root.\n" +
            "- `//*[@class='warning']` — any element with class='warning' (no namespace prefix).\n\n" +
            "## Behavior\n\n" +
            "Loads the UXML with `XDocument.Load`, applies the change, re-serializes with `XmlWriter` (preserves the " +
            "UXML declaration), then `AssetDatabase.ImportAsset` + `Refresh`. Returns `Ok=false` with an explanatory " +
            "error when the selector matches nothing or the XPath itself is malformed.")]
        [Description("Structurally modify a UXML file. Use '" + UxmlReadToolId + "' first to inspect existing content.")]
        public UxmlModifyResult ModifyUxml
        (
            [Description("Asset path under 'Assets/'. Must end with '.uxml'.")]
            string path,
            [Description("Operation: 'replace' | 'append-child' | 'remove-element' | 'set-attribute'.")]
            string operation,
            [Description("XPath-like selector for the target element. " +
                "Use 'ui:' prefix for UnityEngine.UIElements (e.g. '//ui:Label[@name=\"title\"]') " +
                "or 'uie:' prefix for UnityEditor.UIElements.")]
            string? selector = null,
            [Description("New XML content (for 'replace') or child XML fragment (for 'append-child').")]
            string? newContent = null,
            [Description("Attribute name (for 'set-attribute').")]
            string? attributeName = null,
            [Description("Attribute value (for 'set-attribute'). Pass empty string to clear; element keeps the attribute.")]
            string? attributeValue = null
        )
        {
            ValidateAssetPathUxml(path, nameof(path));

            if (string.IsNullOrEmpty(operation))
                throw new ArgumentException(Error.UnknownUxmlOperation(operation ?? "null"), nameof(operation));

            var op = operation.ToLowerInvariant();

            // Pre-validate per-op required inputs so we fail fast before touching disk.
            switch (op)
            {
                case "replace":
                case "append-child":
                    if (string.IsNullOrEmpty(selector))
                        throw new ArgumentException(Error.SelectorRequired(op), nameof(selector));
                    if (newContent == null)
                        throw new ArgumentException(Error.NewContentRequired(op), nameof(newContent));
                    break;

                case "remove-element":
                    if (string.IsNullOrEmpty(selector))
                        throw new ArgumentException(Error.SelectorRequired(op), nameof(selector));
                    break;

                case "set-attribute":
                    if (string.IsNullOrEmpty(selector))
                        throw new ArgumentException(Error.SelectorRequired(op), nameof(selector));
                    if (string.IsNullOrEmpty(attributeName))
                        throw new ArgumentException(Error.AttributeNameRequired(op), nameof(attributeName));
                    break;

                default:
                    throw new ArgumentException(Error.UnknownUxmlOperation(operation), nameof(operation));
            }

            return MainThread.Instance.Run(() =>
            {
                string absPath = ToAbsolutePath(path);
                if (!File.Exists(absPath))
                {
                    return new UxmlModifyResult
                    {
                        Ok = false,
                        AssetPath = path,
                        Error = Error.AssetNotFound(path)
                    };
                }

                XDocument doc;
                try
                {
                    doc = XDocument.Load(absPath, LoadOptions.PreserveWhitespace);
                }
                catch (XmlException ex)
                {
                    return new UxmlModifyResult
                    {
                        Ok = false,
                        AssetPath = path,
                        Error = Error.InvalidUxml(ex.Message)
                    };
                }

                // Build a namespace manager that recognises the conventional 'ui:' / 'uie:'
                // prefixes regardless of what's declared at the root. UXML files always
                // bind ui -> UnityEngine.UIElements; clients write selectors against that.
                var nsManager = BuildUxmlNamespaceResolver(doc);

                List<XElement> matches;
                try
                {
                    matches = doc.XPathSelectElements(selector!, nsManager).ToList();
                }
                catch (XPathException ex)
                {
                    return new UxmlModifyResult
                    {
                        Ok = false,
                        AssetPath = path,
                        Error = Error.SelectorInvalid(selector!, ex.Message)
                    };
                }

                if (matches.Count == 0)
                {
                    return new UxmlModifyResult
                    {
                        Ok = false,
                        AssetPath = path,
                        AffectedCount = 0,
                        Error = Error.SelectorNotFound(selector!)
                    };
                }

                int affected = 0;
                try
                {
                    switch (op)
                    {
                        case "replace":
                        {
                            // Parse the new fragment once, then clone it per match so DOM trees stay disjoint.
                            var fragmentTemplate = ParseFragmentOrThrow(newContent!, doc);
                            foreach (var element in matches)
                            {
                                // Cannot replace the root via ReplaceWith — guard.
                                if (element.Parent == null)
                                {
                                    return new UxmlModifyResult
                                    {
                                        Ok = false,
                                        AssetPath = path,
                                        Error = "'replace' cannot target the document root. Use 'append-child' / 'set-attribute' on the root, or rewrite the whole file via '" + UxmlCreateToolId + "'."
                                    };
                                }
                                element.ReplaceWith(new XElement(fragmentTemplate));
                                affected++;
                            }
                            break;
                        }

                        case "append-child":
                        {
                            var fragmentTemplate = ParseFragmentOrThrow(newContent!, doc);
                            foreach (var element in matches)
                            {
                                element.Add(new XElement(fragmentTemplate));
                                affected++;
                            }
                            break;
                        }

                        case "remove-element":
                        {
                            foreach (var element in matches)
                            {
                                if (element.Parent == null)
                                {
                                    return new UxmlModifyResult
                                    {
                                        Ok = false,
                                        AssetPath = path,
                                        Error = "'remove-element' cannot target the document root."
                                    };
                                }
                                element.Remove();
                                affected++;
                            }
                            break;
                        }

                        case "set-attribute":
                        {
                            // Attribute names in UXML rarely carry a namespace — accept either
                            // "name" (no NS) or "prefix:name" (resolved through nsManager).
                            XName attrName = ResolveAttributeXName(attributeName!, nsManager);
                            string value = attributeValue ?? string.Empty;
                            foreach (var element in matches)
                            {
                                element.SetAttributeValue(attrName, value);
                                affected++;
                            }
                            break;
                        }
                    }
                }
                catch (ArgumentException ex)
                {
                    // Bubbled up from ParseFragmentOrThrow / ResolveAttributeXName.
                    return new UxmlModifyResult
                    {
                        Ok = false,
                        AssetPath = path,
                        Error = ex.Message
                    };
                }

                // Write back with stable formatting. Use XmlWriter so the XML declaration
                // and document indentation survive the round trip.
                var settings = new XmlWriterSettings
                {
                    Indent = true,
                    IndentChars = "    ",
                    OmitXmlDeclaration = doc.Declaration == null,
                    Encoding = new System.Text.UTF8Encoding(false /* no BOM */)
                };
                try
                {
                    using var writer = XmlWriter.Create(absPath, settings);
                    doc.Save(writer);
                }
                catch (Exception ex)
                {
                    return new UxmlModifyResult
                    {
                        Ok = false,
                        AssetPath = path,
                        Error = $"Failed to write modified UXML: {ex.Message}"
                    };
                }

                RefreshAsset(path);

                return new UxmlModifyResult
                {
                    Ok = true,
                    AssetPath = path,
                    AffectedCount = affected
                };
            });
        }

        // ----- UXML modify helpers -----

        /// <summary>
        /// Build an <see cref="IXmlNamespaceResolver"/> that always recognises 'ui:' and
        /// 'uie:' prefixes (Unity's conventional UXML namespaces) and also picks up any
        /// extra prefixes declared on the document root.
        /// </summary>
        internal static XmlNamespaceManager BuildUxmlNamespaceResolver(XDocument doc)
        {
            var nameTable = new NameTable();
            var nsManager = new XmlNamespaceManager(nameTable);

            // Always register the Unity-conventional prefixes — UXML clients rely on these
            // regardless of whether they are also declared on the root element.
            nsManager.AddNamespace("ui", UxmlEngineNamespace);
            nsManager.AddNamespace("uie", UxmlEditorNamespace);

            // Mirror anything else declared on the root so callers can use whatever
            // prefix exists in the file.
            if (doc.Root != null)
            {
                foreach (var attr in doc.Root.Attributes())
                {
                    if (attr.IsNamespaceDeclaration)
                    {
                        string prefix = attr.Name.LocalName == "xmlns" ? string.Empty : attr.Name.LocalName;
                        if (prefix.Length == 0)
                        {
                            // Default namespace — XPath has no concept of a "default" namespace,
                            // so XPath cannot match unprefixed elements that live in a non-empty
                            // default namespace. We still register an empty-prefix entry for
                            // callers that need to reference it explicitly with a placeholder.
                            continue;
                        }
                        if (nsManager.LookupNamespace(prefix) == null)
                            nsManager.AddNamespace(prefix, attr.Value);
                    }
                }
            }

            return nsManager;
        }

        /// <summary>
        /// Parse a UXML fragment into a single <see cref="XElement"/>. The fragment may
        /// omit namespace declarations — they will be inherited from the parent document
        /// at insertion time. Throws ArgumentException for malformed XML or for fragments
        /// that do not have exactly one root element.
        /// </summary>
        internal static XElement ParseFragmentOrThrow(string fragment, XDocument parentDoc)
        {
            // Wrap in a synthetic root that carries the parent document's namespace
            // declarations so XML parsing succeeds even when the fragment uses 'ui:' / 'uie:'
            // prefixes without declaring them.
            var nsDeclarations = new List<string>();
            if (parentDoc.Root != null)
            {
                foreach (var attr in parentDoc.Root.Attributes())
                {
                    if (attr.IsNamespaceDeclaration)
                        nsDeclarations.Add($"{attr.Name}=\"{System.Security.SecurityElement.Escape(attr.Value)}\"");
                }
            }
            if (!nsDeclarations.Any(d => d.StartsWith("xmlns:ui=")))
                nsDeclarations.Add($"xmlns:ui=\"{UxmlEngineNamespace}\"");
            if (!nsDeclarations.Any(d => d.StartsWith("xmlns:uie=")))
                nsDeclarations.Add($"xmlns:uie=\"{UxmlEditorNamespace}\"");

            string wrapped = $"<__fragment__ {string.Join(" ", nsDeclarations)}>{fragment}</__fragment__>";

            XDocument wrappedDoc;
            try
            {
                wrappedDoc = XDocument.Parse(wrapped);
            }
            catch (XmlException ex)
            {
                throw new ArgumentException(Error.InvalidUxml($"Fragment failed to parse: {ex.Message}"));
            }

            var rootChildren = wrappedDoc.Root!.Elements().ToList();
            if (rootChildren.Count == 0)
                throw new ArgumentException(Error.InvalidUxml("Fragment must contain at least one element."));
            if (rootChildren.Count > 1)
                throw new ArgumentException(Error.InvalidUxml("Fragment must contain exactly one root element."));

            return rootChildren[0];
        }

        /// <summary>
        /// Resolve "name" or "prefix:name" into an <see cref="XName"/> using the given
        /// namespace manager. Unknown prefixes throw ArgumentException.
        /// </summary>
        internal static XName ResolveAttributeXName(string raw, XmlNamespaceManager nsManager)
        {
            int colon = raw.IndexOf(':');
            if (colon < 0)
                return XName.Get(raw);

            string prefix = raw.Substring(0, colon);
            string local = raw.Substring(colon + 1);
            string? ns = nsManager.LookupNamespace(prefix);
            if (ns == null)
                throw new ArgumentException(Error.InvalidUxml($"Unknown namespace prefix '{prefix}' in attribute name '{raw}'."));
            return XName.Get(local, ns);
        }
    }
}
