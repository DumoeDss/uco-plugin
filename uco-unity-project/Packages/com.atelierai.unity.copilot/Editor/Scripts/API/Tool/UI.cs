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
using System.ComponentModel;
using System.IO;
using System.Xml.Linq;
using com.AtelierAI.Uco.Framework;
using UnityEditor;
using UnityEngine;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    /// <summary>
    /// Programmatic Unity UI Toolkit (UXML / USS / UIDocument) tools. Each tool either
    /// writes/reads text assets under 'Assets/' or operates on a UIDocument host
    /// GameObject. Heavy lifting (XDocument parsing, AssetDatabase, UIDocument) runs
    /// on the Unity main thread.
    /// </summary>
    [UcoToolType]
    public partial class Tool_UI
    {
        // ----- Shared constants -----

        /// <summary>UXML root namespace used by Unity (xmlns:ui="UnityEngine.UIElements").</summary>
        internal const string UxmlEngineNamespace = "UnityEngine.UIElements";

        /// <summary>UXML editor namespace used by Unity (xmlns:uie="UnityEditor.UIElements").</summary>
        internal const string UxmlEditorNamespace = "UnityEditor.UIElements";

        // ----- Shared path validation helpers -----

        /// <summary>Throws on empty / wrong-extension / out-of-tree UXML asset paths.</summary>
        internal static void ValidateAssetPathUxml(string path, string paramName)
        {
            if (string.IsNullOrEmpty(path))
                throw new ArgumentException(Error.EmptyAssetPath(), paramName);
            if (!path.StartsWith("Assets/", StringComparison.Ordinal))
                throw new ArgumentException(Error.AssetPathMustStartWithAssets(path), paramName);
            if (!path.EndsWith(".uxml", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException(Error.AssetPathMustEndWithUxml(path), paramName);
        }

        /// <summary>Throws on empty / wrong-extension / out-of-tree USS asset paths.</summary>
        internal static void ValidateAssetPathUss(string path, string paramName)
        {
            if (string.IsNullOrEmpty(path))
                throw new ArgumentException(Error.EmptyAssetPath(), paramName);
            if (!path.StartsWith("Assets/", StringComparison.Ordinal))
                throw new ArgumentException(Error.AssetPathMustStartWithAssets(path), paramName);
            if (!path.EndsWith(".uss", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException(Error.AssetPathMustEndWithUss(path), paramName);
        }

        /// <summary>
        /// Convert a project-relative path like "Assets/UI/Main.uxml" into an absolute
        /// path on disk, normalized to forward slashes.
        /// </summary>
        internal static string ToAbsolutePath(string assetPath)
        {
            string projectRoot = Directory.GetCurrentDirectory();
            return Path.Combine(projectRoot, assetPath).Replace('\\', '/');
        }

        /// <summary>Creates parent directory on disk if missing.</summary>
        internal static void EnsureDirectoryExists(string absPath)
        {
            string? directory = Path.GetDirectoryName(absPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);
        }

        /// <summary>
        /// Force-reimport a single asset and refresh the AssetDatabase. Called after every
        /// successful UXML / USS write so that Unity picks up the change immediately.
        /// </summary>
        internal static void RefreshAsset(string assetPath)
        {
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        }

        /// <summary>
        /// Validate that 'content' is well-formed XML. Throws ArgumentException with the
        /// inner XmlException message attached when it is not.
        /// </summary>
        internal static XDocument ParseUxmlOrThrow(string content, string paramName)
        {
            if (content == null)
                throw new ArgumentException(Error.UxmlContentNull(), paramName);
            try
            {
                return XDocument.Parse(content);
            }
            catch (System.Xml.XmlException ex)
            {
                throw new ArgumentException(Error.InvalidUxml(ex.Message), paramName);
            }
        }

        // ----- Error / message helpers -----

        public static class Error
        {
            // -- Path / FS errors --
            public static string EmptyAssetPath()
                => "Asset path is empty. Sample: \"Assets/UI/Main.uxml\" or \"Assets/UI/Main.uss\".";

            public static string AssetPathMustStartWithAssets(string assetPath)
                => $"Asset path must start with 'Assets/'. Path: '{assetPath}'.";

            public static string AssetPathMustEndWithUxml(string assetPath)
                => $"Asset path must end with '.uxml'. Path: '{assetPath}'.";

            public static string AssetPathMustEndWithUss(string assetPath)
                => $"Asset path must end with '.uss'. Path: '{assetPath}'.";

            public static string AssetAlreadyExists(string assetPath)
                => $"Asset already exists at '{assetPath}'. Pass overwrite=true to replace it.";

            public static string AssetNotFound(string assetPath)
                => $"Asset not found at '{assetPath}'.";

            public static string FileReadFailed(string assetPath, string detail)
                => $"Failed to read file at '{assetPath}'. {detail}";

            // -- UXML / XML errors --
            public static string UxmlContentNull()
                => "UXML 'content' must not be null.";

            public static string InvalidUxml(string detail)
                => $"Invalid UXML/XML: {detail}";

            public static string UnknownUxmlOperation(string op)
                => $"Unknown UXML operation '{op}'. Expected one of: " +
                   "'replace' | 'append-child' | 'remove-element' | 'set-attribute'.";

            public static string SelectorRequired(string op)
                => $"'selector' is required for operation '{op}'.";

            public static string SelectorNotFound(string selector)
                => $"No element matched selector '{selector}'.";

            public static string SelectorInvalid(string selector, string detail)
                => $"Invalid selector '{selector}'. {detail}";

            public static string NewContentRequired(string op)
                => $"'newContent' is required for operation '{op}'.";

            public static string AttributeNameRequired(string op)
                => $"'attributeName' is required for operation '{op}'.";

            // -- USS errors --
            public static string UssContentNull()
                => "USS 'content' must not be null.";

            public static string UnknownUssOperation(string op)
                => $"Unknown USS operation '{op}'. Expected one of: " +
                   "'append' | 'replace-all' | 'add-rule' | 'remove-rule'.";

            public static string DeclarationsRequired(string op)
                => $"'declarations' is required for operation '{op}'.";

            // -- UIDocument / runtime errors --
            public static string GameObjectRefRequired()
                => "GameObject reference is required.";

            public static string VisualTreeAssetPathRequired()
                => "'visualTreeAssetPath' is required.";

            public static string VisualTreeAssetNotFound(string path)
                => $"VisualTreeAsset not found at '{path}'. Expected a '.uxml' file imported in the project.";

            public static string PanelSettingsAssetNotFound(string path)
                => $"PanelSettings asset not found at '{path}'. Expected a '.asset' file with a PanelSettings.";

            public static string UIDocumentMissing(string goName)
                => $"No 'UnityEngine.UIElements.UIDocument' component found on GameObject '{goName}'.";

            public static string UQuerySelectorEmpty()
                => "'selector' is empty. Sample: '.button-primary', '#title', 'Label.warning'.";

            public static string EditorRootVisualElementUnavailable()
                => "UIDocument.rootVisualElement is null. UIDocument creates its visual tree at runtime; " +
                   "enter Play Mode (or use a runtime PanelSettings) for the element tree to be populated.";
        }

        // ----- DTOs -----

        /// <summary>Result for `ui-uxml-create`.</summary>
        public class UxmlCreateResult
        {
            [Description("True when the operation completed without an error.")]
            public bool Ok { get; set; }

            [Description("Project-relative asset path that was written. Null on failure.")]
            public string? AssetPath { get; set; }

            [Description("Error message when Ok is false. Null on success.")]
            public string? Error { get; set; }
        }

        /// <summary>Result for `ui-uxml-read`.</summary>
        public class UxmlReadResult
        {
            [Description("True when the operation completed without an error.")]
            public bool Ok { get; set; }

            [Description("Project-relative asset path that was read. Null on failure.")]
            public string? AssetPath { get; set; }

            [Description("Full UXML content as a string. Null when Ok is false.")]
            public string? Content { get; set; }

            [Description("Error message when Ok is false. Null on success.")]
            public string? Error { get; set; }
        }

        /// <summary>Result for `ui-uxml-modify`.</summary>
        public class UxmlModifyResult
        {
            [Description("True when the operation completed without an error.")]
            public bool Ok { get; set; }

            [Description("Project-relative asset path that was modified. Null on failure.")]
            public string? AssetPath { get; set; }

            [Description("Number of elements / attributes affected by the change.")]
            public int? AffectedCount { get; set; }

            [Description("Error message when Ok is false. Null on success.")]
            public string? Error { get; set; }
        }

        /// <summary>Result for `ui-uss-create`.</summary>
        public class UssCreateResult
        {
            [Description("True when the operation completed without an error.")]
            public bool Ok { get; set; }

            [Description("Project-relative asset path that was written. Null on failure.")]
            public string? AssetPath { get; set; }

            [Description("Error message when Ok is false. Null on success.")]
            public string? Error { get; set; }
        }

        /// <summary>Result for `ui-uss-read`.</summary>
        public class UssReadResult
        {
            [Description("True when the operation completed without an error.")]
            public bool Ok { get; set; }

            [Description("Project-relative asset path that was read. Null on failure.")]
            public string? AssetPath { get; set; }

            [Description("Full USS content as a string. Null when Ok is false.")]
            public string? Content { get; set; }

            [Description("Error message when Ok is false. Null on success.")]
            public string? Error { get; set; }
        }

        /// <summary>Result for `ui-uss-modify`.</summary>
        public class UssModifyResult
        {
            [Description("True when the operation completed without an error.")]
            public bool Ok { get; set; }

            [Description("Project-relative asset path that was modified. Null on failure.")]
            public string? AssetPath { get; set; }

            [Description("Number of rules affected by the change (only meaningful for 'add-rule' / 'remove-rule').")]
            public int? AffectedCount { get; set; }

            [Description("Error message when Ok is false. Null on success.")]
            public string? Error { get; set; }
        }

        /// <summary>Result for `ui-document-attach` / `ui-document-detach`.</summary>
        public class DocumentAttachResult
        {
            [Description("True when the operation completed without an error.")]
            public bool Ok { get; set; }

            [Description("Hierarchy path of the target GameObject, when resolved.")]
            public string? GameObjectPath { get; set; }

            [Description("Project-relative asset path of the VisualTreeAsset that was attached. Null when not applicable.")]
            public string? AssetPath { get; set; }

            [Description("Error message when Ok is false. Null on success.")]
            public string? Error { get; set; }
        }

        /// <summary>Result for `ui-element-query`.</summary>
        public class ElementQueryResult
        {
            [Description("True when the operation completed without an error.")]
            public bool Ok { get; set; }

            [Description("Total number of elements matched (before maxResults truncation).")]
            public int TotalCount { get; set; }

            [Description("Matched elements, capped by 'maxResults'.")]
            public ElementInfo[] Elements { get; set; } = Array.Empty<ElementInfo>();

            [Description("Error message when Ok is false. Null on success.")]
            public string? Error { get; set; }
        }

        /// <summary>Shallow snapshot of one VisualElement matched by `ui-element-query`.</summary>
        public class ElementInfo
        {
            [Description("VisualElement.name (string). Empty when the element has no name set.")]
            public string Name { get; set; } = "";

            [Description("Element type — fully qualified name of the C# class (e.g. UnityEngine.UIElements.Label).")]
            public string TypeName { get; set; } = "";

            [Description("USS class names applied to the element (array; empty when none).")]
            public string[] ClassNames { get; set; } = Array.Empty<string>();

            [Description("Text content (only populated for Label / Button / TextField). Null otherwise.")]
            public string? Text { get; set; }

            [Description("Whether the element is currently visible (resolvedStyle.display != None).")]
            public bool Visible { get; set; }

            [Description("Resolved X position in the element's local layout rectangle.")]
            public float X { get; set; }

            [Description("Resolved Y position in the element's local layout rectangle.")]
            public float Y { get; set; }

            [Description("Resolved layout width in pixels.")]
            public float Width { get; set; }

            [Description("Resolved layout height in pixels.")]
            public float Height { get; set; }
        }
    }
}
