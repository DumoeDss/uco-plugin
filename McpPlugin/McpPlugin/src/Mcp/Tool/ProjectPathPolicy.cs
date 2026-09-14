/*
┌────────────────────────────────────────────────────────────────────────┐
│  Author: Ivan Murzak (https://github.com/IvanMurzak)                   │
│  Copyright (c) 2025 Ivan Murzak                                        │
│  Licensed under the MIT License.                                      │
└────────────────────────────────────────────────────────────────────────┘
*/
#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using com.AtelierAI.Uco.Framework.Common.Model;

namespace com.AtelierAI.Uco.Framework
{
    /// <summary>Stable names for the project roots understood by the pilot.</summary>
    public static class ProjectPathRootCategories
    {
        public const string Assets = "Assets";
        public const string Packages = "Packages";
        public const string Library = "Library";
        public const string ProjectSettings = "ProjectSettings";
        public const string UserSettings = "UserSettings";
    }

    /// <summary>
    /// Access intent is also exposed with a project-oriented name for callers
    /// that do not use the authoring descriptor types directly.
    /// </summary>
    public enum ProjectPathAccessIntent
    {
        Read,
        Create,
        Modify,
        Delete,
        Output,
    }

    /// <summary>
    /// Filesystem seam used by <see cref="ProjectPathPolicy"/>. Production
    /// uses <see cref="FileSystemProjectPathCanonicalizer"/>; tests can use a
    /// temporary-project or in-memory implementation without changing policy.
    /// Implementations must fail closed by returning false when a filesystem
    /// fact cannot be determined safely.
    /// </summary>
    public interface IProjectPathCanonicalizer
    {
        string ProjectRoot { get; }
        bool IsCaseSensitive { get; }

        /// <summary>Returns a lexical, absolute path without following links.</summary>
        string Canonicalize(string path);

        bool Exists(string fullPath);
        bool IsDirectory(string fullPath);
        bool IsReparsePoint(string fullPath);
        bool TryResolveReparsePoint(string fullPath, out string resolvedFullPath);
    }

    /// <summary>
    /// OS-backed canonicalizer. Link resolution is intentionally reflective so
    /// the shared McpPlugin assembly remains buildable for netstandard2.1 and
    /// older Unity runtimes. A detected reparse point that cannot be resolved
    /// is reported to policy as unsafe rather than treated as ordinary text.
    /// </summary>
    public sealed class FileSystemProjectPathCanonicalizer : IProjectPathCanonicalizer
    {
        private static readonly MethodInfo? ResolveLinkTargetMethod =
            typeof(FileSystemInfo).GetMethod(
                "ResolveLinkTarget",
                BindingFlags.Instance | BindingFlags.Public,
                binder: null,
                types: new[] { typeof(bool) },
                modifiers: null);

        private static readonly PropertyInfo? LinkTargetProperty =
            typeof(FileSystemInfo).GetProperty("LinkTarget", BindingFlags.Instance | BindingFlags.Public);

        public FileSystemProjectPathCanonicalizer(string projectRoot, bool? caseSensitive = null)
        {
            if (string.IsNullOrWhiteSpace(projectRoot))
                throw new ArgumentException("Project root must be a non-empty path.", nameof(projectRoot));

            ProjectRoot = Canonicalize(projectRoot);
            IsCaseSensitive = caseSensitive ?? !IsWindowsFileSystem();
        }

        public string ProjectRoot { get; }
        public bool IsCaseSensitive { get; }

        public string Canonicalize(string path)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            return Path.GetFullPath(path);
        }

        public bool Exists(string fullPath)
        {
            try
            {
                return File.Exists(fullPath) || Directory.Exists(fullPath);
            }
            catch
            {
                return false;
            }
        }

        public bool IsDirectory(string fullPath)
        {
            try { return Directory.Exists(fullPath); }
            catch { return false; }
        }

        public bool IsReparsePoint(string fullPath)
        {
            try
            {
                if (!Exists(fullPath))
                    return false;

                return (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0;
            }
            catch
            {
                // A path that exists but whose attributes cannot be read is
                // handled as unresolved by policy's conservative probe.
                return true;
            }
        }

        public bool TryResolveReparsePoint(string fullPath, out string resolvedFullPath)
        {
            resolvedFullPath = string.Empty;
            try
            {
                if (!IsReparsePoint(fullPath))
                    return false;

                var info = IsDirectory(fullPath)
                    ? (FileSystemInfo)new DirectoryInfo(fullPath)
                    : new FileInfo(fullPath);

                if (ResolveLinkTargetMethod != null)
                {
                    var resolved = ResolveLinkTargetMethod.Invoke(info, new object[] { true }) as FileSystemInfo;
                    if (resolved != null && !string.IsNullOrWhiteSpace(resolved.FullName))
                    {
                        resolvedFullPath = Canonicalize(resolved.FullName);
                        return true;
                    }
                }

                // LinkTarget is available on newer runtimes even when a final
                // target cannot be resolved. Relative link values are resolved
                // against the link's parent; absolute values remain absolute.
                if (LinkTargetProperty != null)
                {
                    var linkTarget = LinkTargetProperty.GetValue(info) as string;
                    if (!string.IsNullOrWhiteSpace(linkTarget))
                    {
                        var parent = Path.GetDirectoryName(fullPath);
                        var combined = Path.IsPathRooted(linkTarget)
                            ? linkTarget
                            : Path.Combine(parent ?? string.Empty, linkTarget);
                        resolvedFullPath = Canonicalize(combined);
                        return true;
                    }
                }
            }
            catch
            {
                // An unresolved reparse point is an explicit fail-closed case.
            }

            resolvedFullPath = string.Empty;
            return false;
        }

        private static bool IsWindowsFileSystem()
            => Path.DirectorySeparatorChar == '\\'
                || Environment.OSVersion.Platform == PlatformID.Win32NT
                || Environment.OSVersion.Platform == PlatformID.Win32Windows;
    }

    /// <summary>Canonical path returned after policy and reparse validation.</summary>
    public sealed class ProjectPathResolution
    {
        public string InputPath { get; }
        public string FullPath { get; }
        public string CanonicalPath => FullPath;
        public string RelativePath { get; }
        public string DisplayPath => RelativePath;
        public string RootCategory { get; }
        public ProjectPathAccessIntent Intent { get; }
        public AuthoringPathAccessIntent AuthoringIntent => (AuthoringPathAccessIntent)Intent;
        public bool IsReadOnly { get; }
        public bool Exists { get; }
        public bool IsDirectory { get; }
        /// <summary>Opaque, platform-normalized identity of the lexical path.</summary>
        public string LexicalIdentity { get; }
        /// <summary>
        /// Opaque identity of the nearest existing path and every resolved
        /// reparse hop. It is safe to bind or log because no host path is
        /// exposed outside this value.
        /// </summary>
        public string TargetIdentity { get; }

        internal ProjectPathResolution(
            string inputPath,
            string fullPath,
            string relativePath,
            string rootCategory,
            ProjectPathAccessIntent intent,
            bool isReadOnly,
            bool exists,
            bool isDirectory,
            string lexicalIdentity,
            string targetIdentity)
        {
            InputPath = inputPath;
            FullPath = fullPath;
            RelativePath = relativePath;
            RootCategory = rootCategory;
            Intent = intent;
            IsReadOnly = isReadOnly;
            Exists = exists;
            IsDirectory = isDirectory;
            LexicalIdentity = lexicalIdentity;
            TargetIdentity = targetIdentity;
        }
    }

    /// <summary>One canonicalized path binding attached to an invocation.</summary>
    public sealed class CanonicalProjectPathBinding
    {
        public string ArgumentName { get; }
        public ProjectPathResolution Resolution { get; }

        public string CanonicalPath => Resolution.FullPath;
        public string RelativePath => Resolution.RelativePath;

        internal CanonicalProjectPathBinding(string argumentName, ProjectPathResolution resolution)
        {
            ArgumentName = argumentName;
            Resolution = resolution;
        }
    }

    /// <summary>
    /// Stable exception used for all path-policy failures. Details contain
    /// only safe categories; raw absolute or escaped paths are never copied.
    /// </summary>
    public sealed class ProjectPathPolicyException : ToolCallControlException
    {
        public string Reason { get; }
        public string RootCategory { get; }

        public ProjectPathPolicyException(
            string reason,
            string rootCategory,
            string? callId = null,
            string? correlationId = null,
            string? relativePath = null,
            Exception? innerException = null)
            : base(
                ToolCallErrorCodes.PathPolicyViolation,
                "The requested path is not allowed by project policy.",
                retryable: false,
                callId: callId,
                correlationId: correlationId,
                details: CreateDetails(reason, rootCategory, relativePath),
                innerException: innerException)
        {
            Reason = SafeCategory(reason, "unknown_reason");
            RootCategory = SafeCategory(rootCategory, "unknown");
        }

        private static JsonObject CreateDetails(string reason, string rootCategory, string? relativePath)
        {
            var details = new JsonObject
            {
                ["reason"] = SafeCategory(reason, "unknown_reason"),
                ["root"] = SafeCategory(rootCategory, "unknown"),
            };
            if (!string.IsNullOrWhiteSpace(relativePath) && IsSafeRelative(relativePath))
                details["relativePath"] = Bound(relativePath.Replace('\\', '/'));
            return details;
        }

        private static bool IsSafeRelative(string path)
            => !path.StartsWith("/", StringComparison.Ordinal)
                && !path.StartsWith("\\", StringComparison.Ordinal)
                && !(path.Length > 1 && path[1] == ':')
                && !path.Contains("..", StringComparison.Ordinal);

        private static string Bound(string value)
            => value.Length <= 80 ? value : value.Substring(0, 80);

        private static string SafeCategory(string? value, string fallback)
        {
            if (string.IsNullOrWhiteSpace(value)
                || value.Length > 64
                || value == "."
                || value == "..")
                return fallback;

            foreach (var character in value)
            {
                if (!(char.IsLetterOrDigit(character) || character == '-' || character == '_' || character == '.'))
                    return fallback;
            }

            return value;
        }
    }

    /// <summary>
    /// Ambient policy handle for authoring adapters that derive a trusted path
    /// from an inspected Unity object rather than from a caller argument (for
    /// example, scene-save's existing scene path). The handle is installed
    /// only while the safety middleware is executing the approved invocation.
    /// </summary>
    public static class ProjectPathPolicyContext
    {
        private static readonly AsyncLocal<ProjectPathPolicy?> CurrentValue = new();

        public static ProjectPathPolicy? Current => CurrentValue.Value;

        internal static IDisposable Push(ProjectPathPolicy policy)
        {
            if (policy == null)
                throw new ArgumentNullException(nameof(policy));

            var previous = CurrentValue.Value;
            CurrentValue.Value = policy;
            return new Scope(previous);
        }

        private sealed class Scope : IDisposable
        {
            private readonly ProjectPathPolicy? _previous;
            private int _disposed;

            public Scope(ProjectPathPolicy? previous) => _previous = previous;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                    CurrentValue.Value = _previous;
            }
        }
    }

    /// <summary>
    /// Central project/authoring path policy. The only writable built-in root
    /// is Assets; Packages is always read-only for this pilot, and a caller
    /// cannot change that through a per-call binding. Additional output roots
    /// must be registered by trusted project configuration first.
    /// </summary>
    public sealed class ProjectPathPolicy
    {
        private sealed class RootBinding
        {
            public string Category { get; }
            public string FullPath { get; }
            public bool ReadOnly { get; }
            public bool OutputRoot { get; }

            public RootBinding(string category, string fullPath, bool readOnly, bool outputRoot)
            {
                Category = category;
                FullPath = fullPath;
                ReadOnly = readOnly;
                OutputRoot = outputRoot;
            }
        }

        private readonly Dictionary<string, RootBinding> _roots =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly IProjectPathCanonicalizer _canonicalizer;

        public ProjectPathPolicy(string projectRoot)
            : this(new FileSystemProjectPathCanonicalizer(projectRoot))
        {
        }

        public ProjectPathPolicy(IProjectPathCanonicalizer canonicalizer)
        {
            _canonicalizer = canonicalizer ?? throw new ArgumentNullException(nameof(canonicalizer));
            ProjectRoot = _canonicalizer.Canonicalize(_canonicalizer.ProjectRoot);

            AddBuiltIn(ProjectPathRootCategories.Assets, writable: true);
            AddBuiltIn(ProjectPathRootCategories.Packages, writable: false);
            AddBuiltIn(ProjectPathRootCategories.Library, writable: false);
            AddBuiltIn(ProjectPathRootCategories.ProjectSettings, writable: false);
            AddBuiltIn(ProjectPathRootCategories.UserSettings, writable: false);
        }

        public string ProjectRoot { get; }
        public string DefaultAuthoringRoot => _roots[ProjectPathRootCategories.Assets].FullPath;
        public IProjectPathCanonicalizer Canonicalizer => _canonicalizer;

        /// <summary>
        /// Registers a trusted capture/output root. The root name is policy
        /// configuration, not a caller-controlled path or write permission.
        /// </summary>
        public void RegisterOutputRoot(string rootCategory, string fullPath)
        {
            if (string.IsNullOrWhiteSpace(rootCategory) || !IsSafeCategory(rootCategory))
                throw new ArgumentException("Output root category must be a safe identifier.", nameof(rootCategory));
            if (IsBuiltIn(rootCategory))
                throw new ArgumentException("Built-in project roots cannot be replaced.", nameof(rootCategory));
            if (string.IsNullOrWhiteSpace(fullPath) || !IsAbsoluteLike(fullPath))
                throw new ArgumentException("Output root must be an absolute filesystem path.", nameof(fullPath));

            string canonical;
            try { canonical = _canonicalizer.Canonicalize(fullPath); }
            catch (Exception ex)
            {
                throw new ArgumentException("Output root cannot be canonicalized.", nameof(fullPath), ex);
            }

            _roots[rootCategory] = new RootBinding(rootCategory, canonical, readOnly: false, outputRoot: true);
        }

        /// <summary>Resolve one path with an explicit access intent.</summary>
        public ProjectPathResolution Resolve(
            string inputPath,
            AuthoringPathAccessIntent intent,
            string? declaredRoot = null)
            => Resolve(inputPath, (ProjectPathAccessIntent)intent, declaredRoot);

        /// <summary>Resolve one path with the project-oriented intent enum.</summary>
        public ProjectPathResolution Resolve(
            string inputPath,
            ProjectPathAccessIntent intent,
            string? declaredRoot = null)
        {
            if (inputPath == null)
                throw Violation("empty", declaredRoot ?? ProjectPathRootCategories.Assets);

            var normalizedInput = NormalizeInput(inputPath, declaredRoot);
            var parts = SplitAndNormalize(normalizedInput, declaredRoot);
            var root = SelectRoot(parts, declaredRoot, out var relativeParts);
            ValidateIntent(root, intent);

            var relative = string.Join("/", relativeParts);
            var candidate = Combine(root.FullPath, relativeParts);
            string canonical;
            try
            {
                canonical = _canonicalizer.Canonicalize(candidate);
            }
            catch (Exception ex)
            {
                throw Violation("canonicalization_failed", root.Category, ex);
            }

            EnsureContained(root, canonical);
            var targetIdentity = InspectReparseSegments(root, canonical);

            var exists = SafeExists(canonical, root.Category);
            var isDirectory = exists && SafeIsDirectory(canonical, root.Category);
            var display = BuildDisplayPath(root, canonical, relative);
            return new ProjectPathResolution(
                inputPath,
                canonical,
                display,
                root.Category,
                intent,
                root.OutputRoot ? false : root.ReadOnly || IsReadOnlyRoot(root.Category),
                exists,
                isDirectory,
                BuildPathIdentity(root, canonical),
                targetIdentity);
        }

        /// <summary>
        /// Re-resolves a previously approved binding immediately before a
        /// write. The old full path is never reused as authority.
        /// </summary>
        public ProjectPathResolution ReResolveBeforeWrite(ProjectPathResolution binding)
        {
            if (binding == null) throw new ArgumentNullException(nameof(binding));
            if (!IsWriteIntent(binding.Intent))
                throw Violation("write_recheck_requires_write_intent", binding.RootCategory, binding.RelativePath);
            var current = Resolve(binding.InputPath, binding.Intent, binding.RootCategory);
            if (!string.Equals(binding.TargetIdentity, current.TargetIdentity, StringComparison.Ordinal))
            {
                throw new ToolCallControlException(
                    ToolCallErrorCodes.ConfirmationStale,
                    "The confirmed project path target changed before execution.",
                    retryable: false,
                    details: new JsonObject { ["reason"] = "path_target_changed" });
            }
            return current;
        }

        public ProjectPathResolution ResolveForWrite(
            string inputPath,
            AuthoringPathAccessIntent intent,
            string? declaredRoot = null)
        {
            var first = Resolve(inputPath, intent, declaredRoot);
            return ReResolveBeforeWrite(first);
        }

        /// <summary>Project-oriented overload of <see cref="ResolveForWrite(string, AuthoringPathAccessIntent, string?)"/>.</summary>
        public ProjectPathResolution ResolveForWrite(
            string inputPath,
            ProjectPathAccessIntent intent,
            string? declaredRoot = null)
            => ResolveForWrite(inputPath, (AuthoringPathAccessIntent)intent, declaredRoot);

        /// <summary>
        /// Applies descriptor-declared bindings to one invocation. String path
        /// arguments are replaced with their canonical project-relative form;
        /// the full path remains available through the invocation binding map.
        /// </summary>
        public AuthoringInvocation ApplyToInvocation(AuthoringInvocation invocation)
        {
            if (invocation == null) throw new ArgumentNullException(nameof(invocation));
            var descriptor = invocation.Descriptor;
            if (descriptor == null || descriptor.PathBindings == null || descriptor.PathBindings.Count == 0)
                return invocation;

            var arguments = new Dictionary<string, JsonElement>(invocation.Arguments, StringComparer.Ordinal);
            var bindings = new Dictionary<string, CanonicalProjectPathBinding>(StringComparer.OrdinalIgnoreCase);
            foreach (var binding in descriptor.PathBindings)
            {
                if (binding == null || string.IsNullOrWhiteSpace(binding.ArgumentName))
                    throw Violation("invalid_binding", descriptor.MutationKind.ToString());

                if (!TryGetArgument(arguments, binding.ArgumentName, out var key, out var value))
                {
                    if (binding.Required)
                        throw Violation("required_path_missing", binding.RootCategory ?? ProjectPathRootCategories.Assets);
                    continue;
                }

                if (value.ValueKind == JsonValueKind.Null && !binding.Required)
                    continue;
                if (value.ValueKind != JsonValueKind.String)
                    throw Violation("path_argument_not_string", binding.RootCategory ?? ProjectPathRootCategories.Assets);

                var raw = value.GetString();
                if (raw == null)
                    throw Violation("path_argument_not_string", binding.RootCategory ?? ProjectPathRootCategories.Assets);
                if (!binding.Required && raw.Length == 0)
                    continue;

                var resolution = Resolve(raw, binding.Intent, binding.RootCategory);
                if (bindings.ContainsKey(binding.ArgumentName))
                    throw Violation("duplicate_binding", binding.RootCategory ?? ProjectPathRootCategories.Assets);
                bindings[binding.ArgumentName] = new CanonicalProjectPathBinding(binding.ArgumentName, resolution);
                arguments[key] = StringJsonElement(resolution.RelativePath);
            }

            invocation.SetCanonicalArguments(arguments, bindings);
            return invocation;
        }

        /// <summary>
        /// Re-resolves the canonical bindings for a write immediately before
        /// the runner is called. Every binding is looked up again from the
        /// original caller spelling; the old full path is never trusted.
        /// </summary>
        public AuthoringInvocation ReResolveInvocationBeforeWrite(AuthoringInvocation invocation)
        {
            if (invocation == null) throw new ArgumentNullException(nameof(invocation));
            var descriptor = invocation.Descriptor;
            if (descriptor == null || descriptor.PathBindings == null || descriptor.PathBindings.Count == 0)
                return invocation;

            // A caller cannot manufacture a write authority by invoking this
            // method directly with an unprepared invocation.
            if (!invocation.HasCanonicalArguments)
                return ApplyToInvocation(invocation);

            var arguments = new Dictionary<string, JsonElement>(invocation.Arguments, StringComparer.Ordinal);
            var bindings = new Dictionary<string, CanonicalProjectPathBinding>(StringComparer.OrdinalIgnoreCase);
            foreach (var declaration in descriptor.PathBindings)
            {
                if (declaration == null || string.IsNullOrWhiteSpace(declaration.ArgumentName))
                    throw Violation("invalid_binding", descriptor.MutationKind.ToString());

                if (!TryGetArgument(invocation.RawArguments, declaration.ArgumentName, out _, out var rawValue))
                {
                    if (declaration.Required)
                        throw Violation("required_path_missing", declaration.RootCategory ?? ProjectPathRootCategories.Assets);
                    continue;
                }

                if (rawValue.ValueKind == JsonValueKind.Null && !declaration.Required)
                    continue;
                if (rawValue.ValueKind != JsonValueKind.String)
                    throw Violation("path_argument_not_string", declaration.RootCategory ?? ProjectPathRootCategories.Assets);

                var raw = rawValue.GetString();
                if (raw == null)
                    throw Violation("path_argument_not_string", declaration.RootCategory ?? ProjectPathRootCategories.Assets);
                if (!declaration.Required && raw.Length == 0)
                    continue;

                var resolution = Resolve(raw, declaration.Intent, declaration.RootCategory);
                if (bindings.ContainsKey(declaration.ArgumentName))
                    throw Violation("duplicate_binding", declaration.RootCategory ?? ProjectPathRootCategories.Assets);
                if (invocation.PathBindings.TryGetValue(declaration.ArgumentName, out var previous)
                    && !string.Equals(
                        previous.Resolution.TargetIdentity,
                        resolution.TargetIdentity,
                        StringComparison.Ordinal))
                {
                    throw new ToolCallControlException(
                        ToolCallErrorCodes.ConfirmationStale,
                        "The confirmed project path target changed before execution.",
                        retryable: false,
                        callId: invocation.Context.CallId,
                        correlationId: invocation.Context.CorrelationId,
                        details: new JsonObject { ["reason"] = "path_target_changed" });
                }
                bindings[declaration.ArgumentName] = new CanonicalProjectPathBinding(declaration.ArgumentName, resolution);

                if (TryGetArgument(arguments, declaration.ArgumentName, out var key, out _))
                    arguments[key] = StringJsonElement(resolution.RelativePath);
            }

            invocation.SetCanonicalArguments(arguments, bindings);
            return invocation;
        }

        public bool TryResolve(
            string inputPath,
            AuthoringPathAccessIntent intent,
            out ProjectPathResolution? resolution,
            out ProjectPathPolicyException? error,
            string? declaredRoot = null)
        {
            try
            {
                resolution = Resolve(inputPath, intent, declaredRoot);
                error = null;
                return true;
            }
            catch (ProjectPathPolicyException exception)
            {
                resolution = null;
                error = exception;
                return false;
            }
        }

        private void AddBuiltIn(string category, bool writable)
        {
            var fullPath = _canonicalizer.Canonicalize(Path.Combine(ProjectRoot, category));
            _roots[category] = new RootBinding(category, fullPath, readOnly: !writable, outputRoot: false);
        }

        private RootBinding SelectRoot(
            IReadOnlyList<string> parts,
            string? declaredRoot,
            out IReadOnlyList<string> relativeParts)
        {
            RootBinding root;
            var first = parts.Count == 0 ? null : parts[0];
            if (!string.IsNullOrWhiteSpace(declaredRoot))
            {
                if (!_roots.TryGetValue(declaredRoot, out root!))
                    throw Violation("unknown_declared_root", "unknown");

                if (first != null && _roots.ContainsKey(first)
                    && !string.Equals(first, root.Category, StringComparison.OrdinalIgnoreCase))
                    throw Violation("declared_root_mismatch", root.Category);

                relativeParts = first != null && string.Equals(first, root.Category, StringComparison.OrdinalIgnoreCase)
                    ? parts.Skip(1).ToArray()
                    : parts;
                return root;
            }

            if (first != null && _roots.TryGetValue(first, out root!))
            {
                relativeParts = parts.Skip(1).ToArray();
                return root;
            }

            root = _roots[ProjectPathRootCategories.Assets];
            relativeParts = parts;
            return root;
        }

        private void ValidateIntent(RootBinding root, ProjectPathAccessIntent intent)
        {
            if (intent < ProjectPathAccessIntent.Read || intent > ProjectPathAccessIntent.Output)
                throw Violation("invalid_access_intent", root.Category);

            if (root.OutputRoot && intent != ProjectPathAccessIntent.Output)
                throw Violation("output_root_requires_output_intent", root.Category);

            if (!root.OutputRoot && intent == ProjectPathAccessIntent.Output)
                throw Violation("output_root_not_registered", root.Category);

            if (string.Equals(root.Category, ProjectPathRootCategories.Packages, StringComparison.OrdinalIgnoreCase)
                && intent != ProjectPathAccessIntent.Read)
                throw Violation("package_write_forbidden", root.Category);

            if (IsForbiddenWriteRoot(root.Category) && intent != ProjectPathAccessIntent.Read)
                throw Violation("non_authoring_root_write_forbidden", root.Category);
        }

        private void EnsureContained(RootBinding root, string candidate)
        {
            var rootPath = TrimSeparators(root.FullPath);
            var candidatePath = TrimSeparators(candidate);
            var comparison = _canonicalizer.IsCaseSensitive
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;
            if (string.Equals(rootPath, candidatePath, comparison)) return;

            var prefix = rootPath + Path.DirectorySeparatorChar;
            var alternatePrefix = rootPath + Path.AltDirectorySeparatorChar;
            if (!candidatePath.StartsWith(prefix, comparison)
                && !candidatePath.StartsWith(alternatePrefix, comparison))
                throw Violation("containment", root.Category);
        }

        private string InspectReparseSegments(RootBinding root, string candidate)
        {
            var rootPath = TrimSeparators(root.FullPath);
            var candidatePath = TrimSeparators(candidate);
            var comparison = _canonicalizer.IsCaseSensitive
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;
            var suffix = string.Equals(rootPath, candidatePath, comparison)
                ? string.Empty
                : candidatePath.Substring(rootPath.Length).TrimStart('\\', '/');
            var segments = suffix.Length == 0
                ? Array.Empty<string>()
                : suffix.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);

            var identityMaterial = new StringBuilder(256);
            var current = rootPath;
            var nearestExisting = current;
            var visited = new HashSet<string>(comparison == StringComparison.Ordinal
                ? StringComparer.Ordinal
                : StringComparer.OrdinalIgnoreCase);
            var reparseCount = 0;
            InspectOne(root, current, visited, identityMaterial, ref reparseCount);
            foreach (var segment in segments)
            {
                current = Path.Combine(current, segment);
                if (!SafeExists(current, root.Category))
                    break;
                nearestExisting = current;
                InspectOne(root, current, visited, identityMaterial, ref reparseCount);
            }

            AppendIdentityPath(identityMaterial, "nearest", root, nearestExisting);
            return HashIdentity(identityMaterial.ToString());
        }

        private void InspectOne(
            RootBinding root,
            string path,
            ISet<string> visited,
            StringBuilder identityMaterial,
            ref int reparseCount)
        {
            bool isReparse;
            try { isReparse = _canonicalizer.IsReparsePoint(path); }
            catch (Exception ex) { throw Violation("reparse_metadata_unavailable", root.Category, ex); }
            if (!isReparse) return;

            if (++reparseCount > 64)
                throw Violation("reparse_chain_too_deep", root.Category);

            var key = TrimSeparators(path);
            if (!visited.Add(key))
                throw Violation("reparse_cycle", root.Category);

            string target;
            bool resolved;
            try
            {
                resolved = _canonicalizer.TryResolveReparsePoint(path, out target!);
            }
            catch (Exception ex)
            {
                throw Violation("reparse_resolution_failed", root.Category, ex);
            }

            if (!resolved || string.IsNullOrWhiteSpace(target))
                throw Violation("reparse_unresolved", root.Category);

            string canonicalTarget;
            try { canonicalTarget = _canonicalizer.Canonicalize(target); }
            catch (Exception ex) { throw Violation("reparse_target_invalid", root.Category, ex); }
            EnsureContained(root, canonicalTarget);
            AppendIdentityPath(identityMaterial, "link", root, path);
            AppendIdentityPath(identityMaterial, "target", root, canonicalTarget);

            // A link may point at another link. Resolve the chain while the
            // visited set is available so cycles and an eventual outside
            // target fail closed as well.
            if (SafeExists(canonicalTarget, root.Category))
                InspectOne(root, canonicalTarget, visited, identityMaterial, ref reparseCount);
        }

        private void AppendIdentityPath(
            StringBuilder identityMaterial,
            string kind,
            RootBinding root,
            string canonicalPath)
        {
            var rootPath = TrimSeparators(root.FullPath);
            var path = TrimSeparators(canonicalPath);
            var comparison = _canonicalizer.IsCaseSensitive
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;
            var relative = string.Equals(rootPath, path, comparison)
                ? string.Empty
                : path.Substring(rootPath.Length).TrimStart('\\', '/');
            relative = relative.Replace('\\', '/');
            if (!_canonicalizer.IsCaseSensitive)
                relative = relative.ToUpperInvariant();
            identityMaterial.Append(kind).Append(':').Append(relative.Length).Append(':').Append(relative).Append('|');
        }

        private string BuildPathIdentity(RootBinding root, string canonicalPath)
        {
            var material = new StringBuilder(128);
            AppendIdentityPath(material, "path", root, canonicalPath);
            return HashIdentity(material.ToString());
        }

        private static string HashIdentity(string value)
        {
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value));
            var result = new StringBuilder(71);
            result.Append("sha256-");
            foreach (var valueByte in bytes)
                result.Append(valueByte.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
            return result.ToString();
        }

        private string BuildDisplayPath(RootBinding root, string canonical, string fallbackRelative)
        {
            var rootPath = TrimSeparators(root.FullPath);
            var candidate = TrimSeparators(canonical);
            var comparison = _canonicalizer.IsCaseSensitive
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;
            if (string.Equals(rootPath, candidate, comparison))
                return root.Category;
            if (candidate.StartsWith(rootPath + Path.DirectorySeparatorChar, comparison)
                || candidate.StartsWith(rootPath + Path.AltDirectorySeparatorChar, comparison))
            {
                var suffix = candidate.Substring(rootPath.Length).TrimStart('\\', '/');
                return root.Category + "/" + suffix.Replace('\\', '/');
            }
            return root.Category + "/" + fallbackRelative.Replace('\\', '/');
        }

        private string NormalizeInput(string input, string? declaredRoot)
        {
            if (string.IsNullOrWhiteSpace(input))
                throw Violation("empty", declaredRoot ?? ProjectPathRootCategories.Assets);
            if (input.IndexOf('\0') >= 0)
                throw Violation("nul", declaredRoot ?? ProjectPathRootCategories.Assets);
            if (IsAbsoluteLike(input))
                throw Violation("absolute_or_device_path", declaredRoot ?? ProjectPathRootCategories.Assets);

            var normalized = input.Replace('\\', '/');
            if (normalized.StartsWith("/", StringComparison.Ordinal)
                || normalized.Contains("//", StringComparison.Ordinal))
                throw Violation("malformed_separator", declaredRoot ?? ProjectPathRootCategories.Assets);
            return normalized;
        }

        private IReadOnlyList<string> SplitAndNormalize(string normalized, string? declaredRoot)
        {
            var result = new List<string>();
            var firstPart = normalized.Split('/')[0];
            var hasExplicitRootSegment = !string.IsNullOrWhiteSpace(declaredRoot)
                ? string.Equals(firstPart, declaredRoot, StringComparison.OrdinalIgnoreCase)
                : _roots.ContainsKey(firstPart);
            var minimumParts = hasExplicitRootSegment ? 1 : 0;
            foreach (var rawPart in normalized.Split('/'))
            {
                if (rawPart.Length == 0)
                    throw Violation("malformed_separator", declaredRoot ?? ProjectPathRootCategories.Assets);
                if (rawPart == ".") continue;
                if (rawPart == "..")
                {
                    // The explicit category (`Assets`, `Packages`, etc.) is
                    // itself the root boundary and may not be removed by
                    // traversal normalization.
                    if (result.Count <= minimumParts)
                        throw Violation("traversal", declaredRoot ?? ProjectPathRootCategories.Assets);
                    result.RemoveAt(result.Count - 1);
                    continue;
                }
                if (rawPart.IndexOf('\0') >= 0 || rawPart.IndexOf(':') >= 0)
                    throw Violation("invalid_segment", declaredRoot ?? ProjectPathRootCategories.Assets);
                result.Add(rawPart);
            }
            return result;
        }

        private static string Combine(string root, IReadOnlyList<string> relativeParts)
        {
            var candidate = root;
            foreach (var part in relativeParts)
                candidate = Path.Combine(candidate, part);
            return candidate;
        }

        private static string TrimSeparators(string path)
            => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        private bool SafeExists(string path, string rootCategory)
        {
            try { return _canonicalizer.Exists(path); }
            catch (Exception ex) { throw Violation("filesystem_probe_failed", rootCategory, ex); }
        }

        private bool SafeIsDirectory(string path, string rootCategory)
        {
            try { return _canonicalizer.IsDirectory(path); }
            catch (Exception ex) { throw Violation("filesystem_probe_failed", rootCategory, ex); }
        }

        private static bool IsAbsoluteLike(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            if (value[0] == '/' || value[0] == '\\') return true;
            if (value.Length >= 2 && value[1] == ':') return true;
            if (value.StartsWith("//?", StringComparison.Ordinal)
                || value.StartsWith("//.", StringComparison.Ordinal)
                || value.StartsWith("\\\\?", StringComparison.Ordinal)
                || value.StartsWith("\\\\.", StringComparison.Ordinal))
                return true;
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Scheme))
                return true;
            try { return Path.IsPathRooted(value); }
            catch { return true; }
        }

        private static bool IsWriteIntent(ProjectPathAccessIntent intent)
            => intent != ProjectPathAccessIntent.Read;

        private static bool IsForbiddenWriteRoot(string category)
            => string.Equals(category, ProjectPathRootCategories.Library, StringComparison.OrdinalIgnoreCase)
                || string.Equals(category, ProjectPathRootCategories.ProjectSettings, StringComparison.OrdinalIgnoreCase)
                || string.Equals(category, ProjectPathRootCategories.UserSettings, StringComparison.OrdinalIgnoreCase);

        private static bool IsReadOnlyRoot(string category)
            => !string.Equals(category, ProjectPathRootCategories.Assets, StringComparison.OrdinalIgnoreCase);

        private static bool IsBuiltIn(string category)
            => string.Equals(category, ProjectPathRootCategories.Assets, StringComparison.OrdinalIgnoreCase)
                || string.Equals(category, ProjectPathRootCategories.Packages, StringComparison.OrdinalIgnoreCase)
                || string.Equals(category, ProjectPathRootCategories.Library, StringComparison.OrdinalIgnoreCase)
                || string.Equals(category, ProjectPathRootCategories.ProjectSettings, StringComparison.OrdinalIgnoreCase)
                || string.Equals(category, ProjectPathRootCategories.UserSettings, StringComparison.OrdinalIgnoreCase);

        private static bool IsSafeCategory(string category)
        {
            if (category.Length > 64 || category == "." || category == "..")
                return false;
            foreach (var character in category)
            {
                if (!(char.IsLetterOrDigit(character) || character == '-' || character == '_' || character == '.'))
                    return false;
            }
            return true;
        }

        private static JsonElement StringJsonElement(string value)
        {
            // SerializeToElement is unavailable on the netstandard2.1
            // System.Text.Json surface used by the shared plugin assembly.
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(value));
            return document.RootElement.Clone();
        }

        private static bool TryGetArgument(
            IReadOnlyDictionary<string, JsonElement> arguments,
            string argumentName,
            out string key,
            out JsonElement value)
        {
            if (arguments.TryGetValue(argumentName, out value))
            {
                key = argumentName;
                return true;
            }
            foreach (var pair in arguments)
            {
                if (string.Equals(pair.Key, argumentName, StringComparison.OrdinalIgnoreCase))
                {
                    key = pair.Key;
                    value = pair.Value;
                    return true;
                }
            }
            key = string.Empty;
            value = default;
            return false;
        }

        private ProjectPathPolicyException Violation(
            string reason,
            string rootCategory,
            Exception? innerException = null)
            => new ProjectPathPolicyException(reason, rootCategory, innerException: innerException);

        private ProjectPathPolicyException Violation(
            string reason,
            string rootCategory,
            string relativePath)
            => new ProjectPathPolicyException(reason, rootCategory, relativePath: relativePath);
    }
}
