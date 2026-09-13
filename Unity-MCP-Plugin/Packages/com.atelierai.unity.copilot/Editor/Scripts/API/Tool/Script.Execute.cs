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
using System.Reflection;
using System.Text;
using com.IvanMurzak.McpPlugin;
using com.IvanMurzak.ReflectorNet.Model;
using com.IvanMurzak.ReflectorNet.Utils;
using com.AtelierAI.Unity.Copilot.Utils;
using com.AtelierAI.Unity.Copilot.Editor.Utils;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    public static partial class Tool_Script
    {
        public const string ScriptExecuteToolId = "script-execute";
        [McpPluginTool
        (
            ScriptExecuteToolId,
            Title = "Script / Execute",
            OpenWorldHint = true
        )]
        [McpPluginSkillDescription("Compiles and executes C# code dynamically using Roslyn. " +
            "Supports a full-code mode (default) and a body-only mode — see the skill body for the difference and for how to pass Unity object references as parameters.")]
        [McpPluginSkillBody(
            "## Modes\n\n" +
            "- **Full code mode** (default, `isMethodBody=false`): the `csharpCode` argument must define a complete class with a static method (no top-level statements).\n" +
            "- **Body-only mode** (`isMethodBody=true`): provide only the method body statements. The tool auto-generates the usings, class, and method header.\n\n" +
            "## Returning data (read Unity state)\n\n" +
            "By default body-only mode returns nothing (`returnType=void`, side-effect only). To **read a value back**, set `returnType` to a C# type and end the body with a `return` statement; the value is serialized back to you. Examples: `returnType='UnityEngine.Vector3'` body `return go.transform.position;`; `returnType='int'` body `return Selection.gameObjects.Length;`; `returnType='string'` body `return AssetDatabase.GetAssetPath(go);`. Forgetting the `return` yields a CS0161 compile error — just add it.\n\n" +
            "## Preprocessor symbols (`defines`)\n\n" +
            "The dynamic compilation does NOT inherit the Editor assemblies' preprocessor symbols. Unity APIs guarded with `[Conditional(\"ENABLE_PROFILER\")]` or `#if ENABLE_PROFILER` are silently compiled out unless you pass the symbol explicitly: `defines=[\"ENABLE_PROFILER\"]`. Up to 16 identifiers; malformed entries are dropped.\n\n" +
            "## JSON in dynamic scripts\n\n" +
            "`UnityEngine.JsonUtility` is unreliable for composite fields of types defined in the dynamic script assembly (arrays/lists of custom classes may deserialize as null and serialize as missing). For composite payloads prefer `System.Text.Json` (`JsonSerializer.Serialize/Deserialize`), which works normally.\n\n" +
            "## Passing Unity objects as parameters\n\n" +
            "Unity objects (`GameObject`, `Component`, etc.) can be passed as parameters using their `Ref` types (`GameObjectRef`, `ComponentRef`, etc.) or directly by type:\n\n" +
            "- `UnityEngine.GameObject` — resolves an actual GameObject from value `{\"instanceID\": N}`, `{\"name\": \"...\"}`, or `{\"path\": \"...\"}`.\n" +
            "- `UnityEngine.Component` (or any component subtype) — resolves from `{\"instanceID\": N}`.\n" +
            "- `AIGD.GameObjectRef` — passes a `GameObjectRef` POCO directly; the method body calls `goRef.FindGameObject()` to resolve it.\n" +
            "- `AIGD.ComponentRef` — passes a `ComponentRef` POCO.\n" +
            "- `AIGD.ObjectRef` — passes a base `ObjectRef` POCO.")]
        [Description("Compiles and executes C# code dynamically using Roslyn. " +
            "Supports two modes: full code mode (default) requires a complete class definition, " +
            "while body-only mode (isMethodBody=true) auto-generates the boilerplate so you only " +
            "provide the method body. Unity objects (GameObject, Component, etc.) can be passed as " +
            "parameters using their Ref types (GameObjectRef, ComponentRef, etc.) or directly by type.")]
        public static ScriptExecuteResult Execute
        (
            [Description("C# code to compile and execute. " +
                "In full code mode (default, isMethodBody=false): must define a complete class with a static method. " +
                "Example: 'using UnityEngine; public class Script { public static void Main() { Debug.Log(\"Hello\"); } }'. " +
                "Do NOT use top-level statements. " +
                "In body-only mode (isMethodBody=true): provide only the method body statements. " +
                "The tool auto-generates usings, class, and method header. " +
                "Example body: 'go.SetActive(false);'. " +
                "Custom helper classes can still be defined inline in the body-only string after the main logic, " +
                "but for complex additional class definitions use full code mode instead.")]
            string csharpCode,
            [Description("The name of the class containing the method to execute. " +
                "In body-only mode this becomes the generated class name.")]
            string className = "Script",
            [Description("The name of the method to execute. Must be a static method. " +
                "In body-only mode this becomes the generated method name.")]
            string methodName = "Main",
            [Description("Serialized parameters to pass to the method. Each entry must specify 'name' and 'typeName'. " +
                "Supported parameter types include primitives, strings, and Unity object references: " +
                "- 'UnityEngine.GameObject': resolves an actual GameObject from value '{\"instanceID\": N}', '{\"name\": \"...\"}', or '{\"path\": \"...\"}'. " +
                "- 'UnityEngine.Component' (or any component subtype): resolves from '{\"instanceID\": N}'. " +
                "- 'AIGD.GameObjectRef': passes a GameObjectRef POCO directly; " +
                "  the method body calls goRef.FindGameObject() to resolve it. " +
                "- 'AIGD.ComponentRef': passes a ComponentRef POCO. " +
                "- 'AIGD.ObjectRef': passes a base ObjectRef POCO. " +
                "If the method does not require parameters, leave this empty.")]
            SerializedMemberList? parameters = null,
            [Description("When true, 'csharpCode' is treated as just the method body. " +
                "The tool auto-generates standard using directives (System, UnityEngine, " +
                "AIGD, com.AtelierAI.Unity.Copilot.Runtime.Extensions, UnityEditor), " +
                "the class definition, and the method signature. " +
                "Parameters from the 'parameters' list are automatically added to the method signature using their typeName and name. " +
                "When false (default), 'csharpCode' must be a complete C# compilation unit with class and method definitions.")]
            bool isMethodBody = false,
            [Description("Return type of the generated method in body-only mode. " +
                "Default 'void' = side-effect only, no return value. " +
                "Set to a C# type name to return data: 'int', 'bool', 'string', 'UnityEngine.Vector3', 'System.Collections.Generic.List<string>', etc. " +
                "When non-void, the body MUST end with a 'return <expr>;' statement; the returned value is serialized back to the caller. " +
                "Full-code mode declares its own return type in the code; this is informational there.")]
            string returnType = "void",
            [Description("Execute inside a disposable sandbox scene: a new untitled scene is opened for the work, " +
                "and the previously active scene setup, selection, and dirty state are restored afterwards. " +
                "The result reports whether restoration succeeded. Default false.")]
            bool sandboxScene = false,
            [Description("Optional preprocessor symbols for the Roslyn compilation (e.g. ENABLE_PROFILER). " +
                "Max 16, each a C# identifier of at most 128 characters; duplicates are removed. " +
                "The Editor assemblies' own defines are NOT inherited by default — APIs guarded with " +
                "[Conditional(\"DEFINE\")] or #if DEFINE are compiled out unless you pass the symbol here.")]
            string[]? defines = null
        )
        {
            if (string.IsNullOrEmpty(csharpCode))
                throw new Exception($"'{nameof(csharpCode)}' is null or empty. Please provide valid C# code to execute.");

            if (string.IsNullOrEmpty(className))
                throw new Exception($"'{nameof(className)}' cannot be null or empty.");

            if (string.IsNullOrEmpty(methodName))
                throw new Exception($"'{nameof(methodName)}' cannot be null or empty.");

            string codeToCompile;
            if (isMethodBody)
            {
                codeToCompile = GenerateFullCode(csharpCode, className, methodName, parameters, returnType);
            }
            else
            {
                codeToCompile = csharpCode;
            }

            return MainThread.Instance.Run(() =>
            {
                var logger = UnityLoggerFactory.LoggerFactory.CreateLogger("Tool_Script.Execute");

                // Scene hygiene (COCli-06): the mutation baseline is captured
                // before any scene-affecting work; a sandboxed run additionally
                // captures the full restorable setup and opens a disposable
                // untitled scene for the work.
                var baseline = EditorSceneSandbox.CaptureMutationBaseline();
                var stateCapture = sandboxScene ? EditorSceneSandbox.CaptureState() : null;
                var sandboxSceneHandle = sandboxScene
                    ? EditorSceneSandbox.HandleOf(EditorSceneSandbox.OpenSandboxScene())
                    : 0L;

                var outcome = new ScriptExecuteResult
                {
                    SandboxUsed = sandboxScene ? true : null,
                };
                try
                {
                    // Compile C# code using Roslyn and execute it immediately
                    if (!ExecuteCSharpCode(
                        className: className,
                        methodName: methodName,
                        code: codeToCompile,
                        parameters: parameters,
                        defines: SanitizeDefines(defines),
                        returnValue: out var result,
                        error: out var error,
                        logger: logger))
                    {
                        throw new Exception(error);
                    }

                    if (result is not null)
                    {
                        if (result is SerializedMember serializedResult)
                        {
                            outcome.Value = serializedResult;
                        }
                        else
                        {
                            var reflector = UnityCopilotPluginEditor.Instance.Reflector ?? throw new Exception("Reflector is not available.");
                            outcome.Value = reflector.Serialize(
                                obj: result,
                                logger: logger);
                        }
                    }
                }
                finally
                {
                    // Restore runs even when the script failed, so a throwing
                    // probe still cannot leave the sandbox scene behind.
                    if (stateCapture != null)
                    {
                        var sandbox = EditorSceneSandbox.FindOpenSceneByHandle(sandboxSceneHandle);
                        var restore = EditorSceneSandbox.RestoreCapturedState(stateCapture, sandbox);
                        outcome.SandboxRestored = restore.Restored;
                        outcome.SandboxRestoreCause = restore.Cause;
                    }

                    // The mutation diff runs after restore so a fully restored
                    // sandbox reports clean, while leaked dirt stays visible.
                    var report = EditorSceneSandbox.DiffMutation(baseline);
                    outcome.Mutated = report.Mutated;
                    outcome.MutatedScenes = report.MutatedScenes;
                }

                return outcome;
            });
        }

        static string GenerateFullCode(
            string methodBody,
            string className,
            string methodName,
            SerializedMemberList? parameters,
            string returnType = "void")
        {
            var sb = new StringBuilder();

            // Standard using directives
            sb.AppendLine("using System;");
            sb.AppendLine("using System.Collections;");
            sb.AppendLine("using System.Collections.Generic;");
            sb.AppendLine("using System.Linq;");
            sb.AppendLine("using UnityEngine;");
            // UnityEngine.UI ships in com.unity.ugui — only emit the using when its assembly
            // is loaded; otherwise Roslyn fails with CS0234 when the package isn't installed.
            if (IsAssemblyLoaded("UnityEngine.UI"))
                sb.AppendLine("using UnityEngine.UI;");
            sb.AppendLine("using UnityEngine.SceneManagement;");
            sb.AppendLine("using AIGD;");
            sb.AppendLine("using com.AtelierAI.Unity.Copilot.Runtime.Extensions;");
            sb.AppendLine("using UnityEditor;");
            sb.AppendLine();

            // Build method parameter list from the parameters SerializedMemberList
            var methodParams = parameters != null && parameters.Count > 0
                ? string.Join(", ", parameters.Select(p => $"{p.typeName ?? "object"} {p.name ?? "param"}"))
                : "";

            sb.AppendLine($"public class {className}");
            sb.AppendLine("{");
            sb.AppendLine($"    public static {returnType} {methodName}({methodParams})");
            sb.AppendLine("    {");

            // Indent each line of the method body
            foreach (var line in methodBody.Split('\n'))
                sb.AppendLine($"        {line.TrimEnd()}");

            sb.AppendLine("    }");
            sb.AppendLine("}");

            return sb.ToString();
        }

        static bool IsAssemblyLoaded(string assemblyName)
            => AssemblyUtils.AllAssemblies.Any(a =>
                string.Equals(a.GetName().Name, assemblyName, StringComparison.Ordinal));

        static readonly System.Text.RegularExpressions.Regex DefineSymbolPattern =
            new("^[A-Za-z_][A-Za-z0-9_]*$", System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// COCli-13: bound and validate caller-supplied preprocessor symbols.
        /// Invalid entries are dropped (never fail the whole compilation on a
        /// malformed symbol); the surviving set is de-duplicated, capped at 16
        /// symbols of at most 128 characters each.
        /// </summary>
        static string[]? SanitizeDefines(string[]? defines)
        {
            if (defines == null || defines.Length == 0)
                return null;
            var accepted = new List<string>(defines.Length);
            foreach (var raw in defines)
            {
                if (string.IsNullOrEmpty(raw))
                    continue;
                var symbol = raw.Trim();
                if (symbol.Length == 0 || symbol.Length > 128)
                    continue;
                if (!DefineSymbolPattern.IsMatch(symbol))
                    continue;
                if (!accepted.Contains(symbol, StringComparer.Ordinal))
                    accepted.Add(symbol);
                if (accepted.Count >= 16)
                    break;
            }
            return accepted.Count > 0 ? accepted.ToArray() : null;
        }

        static bool ExecuteCSharpCode(
            string className,
            string methodName,
            string code,
            SerializedMemberList? parameters,
            string[]? defines,
            out object? returnValue,
            out string? error,
            ILogger? logger = null)
        {
            if (string.IsNullOrEmpty(className))
            {
                returnValue = null;
                error = $"'{nameof(className)}' cannot be null or empty.";
                return false;
            }
            if (string.IsNullOrEmpty(methodName))
            {
                returnValue = null;
                error = $"'{nameof(methodName)}' cannot be null or empty.";
                return false;
            }

            var reflector = UnityCopilotPluginEditor.Instance.Reflector ?? throw new Exception("Reflector is not available.");

            var parsedParameters = parameters
                ?.Select(p => reflector.Deserialize(
                    data: p,
                    logger: logger))
                ?.ToArray();

            // COCli-13: the Editor assemblies' preprocessor symbols are not
            // inherited automatically — the caller must pass them explicitly
            // (e.g. ENABLE_PROFILER) so [Conditional]/#if-guarded Unity APIs
            // are not silently compiled out of the dynamic script.
            var parseOptions = defines is { Length: > 0 }
                ? CSharpParseOptions.Default.WithPreprocessorSymbols(defines)
                : CSharpParseOptions.Default;
            var compilation = CSharpCompilation.Create(
                assemblyName: "DynamicAssembly",
                syntaxTrees: new[] { CSharpSyntaxTree.ParseText(code, options: parseOptions) },
                references: AssemblyUtils.AllAssemblies
                    .Where(a => !a.IsDynamic) // Exclude dynamic assemblies
                    .Where(a => !string.IsNullOrEmpty(a.Location))
                    .Select(a =>
                    {
                        try
                        {
                            return MetadataReference.CreateFromFile(a.Location);
                        }
                        catch (DirectoryNotFoundException ex)
                        {
                            logger?.LogWarning(ex, "Directory not found for assembly '{AssemblyName}' at '{Location}': {Error}",
                                a.GetName().Name, a.Location, ex.Message);
                            return null;
                        }
                        catch (FileNotFoundException ex)
                        {
                            logger?.LogWarning(ex, "File not found for assembly '{AssemblyName}' at '{Location}': {Error}",
                                a.GetName().Name, a.Location, ex.Message);
                            return null;
                        }
                        catch (Exception ex)
                        {
                            logger?.LogWarning(ex, "Failed to load metadata reference for assembly '{AssemblyName}' at '{Location}': {Error}",
                                a.GetName().Name, a.Location, ex.Message);
                            return null;
                        }
                    })
                    .OfType<MetadataReference>()
                    .ToArray(),
                options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
            );

            using (var ms = new MemoryStream())
            {
                var result = compilation.Emit(ms);
                if (!result.Success)
                {
                    // Separate errors from warnings, cap the error list, and hint that later
                    // errors often cascade from the first. Raw Roslyn dumps overwhelm the agent
                    // when a single missing semicolon produces a wall of CS1xxx messages.
                    var diagErrors = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
                    var diagWarnings = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Warning).ToList();
                    var diagBuilder = new StringBuilder();
                    diagBuilder.AppendLine($"Compilation failed: {diagErrors.Count} error(s), {diagWarnings.Count} warning(s).");
                    diagBuilder.AppendLine("Fix the first error(s) first — later ones frequently cascade from the earliest.");
                    const int maxErrors = 8;
                    foreach (var d in diagErrors.Take(maxErrors))
                        diagBuilder.AppendLine(d.ToString());
                    if (diagErrors.Count > maxErrors)
                        diagBuilder.AppendLine($"... ({diagErrors.Count - maxErrors} more error(s) omitted; resolve the ones above and re-run).");
                    if (diagWarnings.Count > 0)
                    {
                        diagBuilder.AppendLine($"Warnings (showing up to 3 of {diagWarnings.Count} — e.g. obsolete-API hints worth heeding):");
                        foreach (var d in diagWarnings.Take(3))
                            diagBuilder.AppendLine(d.ToString());
                    }
                    error = diagBuilder.ToString();
                    returnValue = null;
                    return false;
                }
                ms.Seek(0, SeekOrigin.Begin);
                var assembly = Assembly.Load(ms.ToArray());
                var type = ResolveCompiledType(assembly, className, out var typeError);
                if (type == null)
                {
                    error = typeError;
                    returnValue = null;
                    return false;
                }
                var method = type.GetMethod(methodName);
                if (method == null)
                {
                    error = $"Method '{methodName}' not found in class '{className}'.";
                    returnValue = null;
                    return false;
                }
                try
                {
                    returnValue = method.Invoke(null, parsedParameters);
                    error = null;
                    return true;
                }
                catch (TargetInvocationException ex)
                {
                    error = $"Execution failed. TargetInvocationException: {ex.InnerException?.GetType().FullName ?? ex.GetType().FullName}: {ex.InnerException?.Message ?? ex.Message}\n{ex.InnerException?.StackTrace ?? ex.StackTrace}";
                    returnValue = null;
                    return false;
                }
                catch (Exception ex)
                {
                    error = $"Execution failed: {ex.GetType().FullName}: {ex.InnerException?.Message ?? ex.Message}\n{ex.InnerException?.StackTrace ?? ex.StackTrace}";
                    returnValue = null;
                    return false;
                }
            }
        }

        internal static Type? ResolveCompiledType(Assembly assembly, string className, out string? error)
        {
            var exact = assembly.GetType(className, throwOnError: false, ignoreCase: false);
            if (exact != null)
            {
                error = null;
                return exact;
            }

            var simpleMatches = assembly.GetTypes()
                .Where(type => string.Equals(type.Name, className, StringComparison.Ordinal))
                .OrderBy(type => type.FullName, StringComparer.Ordinal)
                .ToArray();
            if (simpleMatches.Length == 1)
            {
                error = null;
                return simpleMatches[0];
            }
            if (simpleMatches.Length > 1)
            {
                const int maxCandidates = 8;
                var candidates = simpleMatches.Take(maxCandidates)
                    .Select(type => type.FullName ?? type.Name);
                var suffix = simpleMatches.Length > maxCandidates
                    ? $", ... ({simpleMatches.Length - maxCandidates} more)"
                    : string.Empty;
                error = $"Class name '{className}' is ambiguous. Use an exact fully qualified name. " +
                    $"Candidates: {string.Join(", ", candidates)}{suffix}.";
                return null;
            }

            var available = assembly.GetTypes()
                .Where(type => type.IsClass)
                .Select(type => type.FullName ?? type.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .Take(8);
            error = $"Class '{className}' not found in the compiled assembly. " +
                $"Available classes: {string.Join(", ", available)}.";
            return null;
        }
    }
}
