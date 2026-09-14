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
using System.Linq;
using System.Reflection;
using System.Text;
using com.IvanMurzak.McpPlugin;
using com.IvanMurzak.ReflectorNet.Model;
using com.IvanMurzak.ReflectorNet.Utils;
using com.AtelierAI.Unity.Copilot.Utils;
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
        public static SerializedMember? Execute
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
            string returnType = "void"
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
                if (csharpCode.Contains(className) == false)
                    throw new Exception($"'{nameof(csharpCode)}' does not contain class '{className}'. Please ensure the class is defined in the provided code.");

                if (csharpCode.Contains(methodName) == false)
                    throw new Exception($"'{nameof(csharpCode)}' does not contain method '{methodName}'. Please ensure the method is defined in the provided code.");

                codeToCompile = csharpCode;
            }

            return MainThread.Instance.Run(() =>
            {
                var logger = UnityLoggerFactory.LoggerFactory.CreateLogger("Tool_Script.Execute");

                // Compile C# code using Roslyn and execute it immediately
                if (!ExecuteCSharpCode(
                    className: className,
                    methodName: methodName,
                    code: codeToCompile,
                    parameters: parameters,
                    returnValue: out var result,
                    error: out var error,
                    logger: logger))
                {
                    throw new Exception(error);
                }

                if (result is null)
                    return null;

                if (result is SerializedMember serializedResult)
                    return serializedResult;

                var reflector = UnityCopilotPluginEditor.Instance.Reflector ?? throw new Exception("Reflector is not available.");

                return reflector.Serialize(
                    obj: result,
                    logger: logger);
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

        static bool ExecuteCSharpCode(
            string className,
            string methodName,
            string code,
            SerializedMemberList? parameters,
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

            var compilation = CSharpCompilation.Create(
                assemblyName: "DynamicAssembly",
                syntaxTrees: new[] { CSharpSyntaxTree.ParseText(code) },
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
                var type = assembly.GetType(className);
                if (type == null)
                {
                    error = $"Class '{className}' not found in the compiled assembly.";
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
                    error = $"Execution failed. TargetInvocationException: {ex.InnerException?.Message ?? ex.Message}\n{ex.InnerException?.StackTrace ?? ex.StackTrace}";
                    returnValue = null;
                    return false;
                }
                catch (Exception ex)
                {
                    error = $"Execution failed: {ex.InnerException?.Message ?? ex.Message}\n{ex.InnerException?.StackTrace ?? ex.StackTrace}";
                    returnValue = null;
                    return false;
                }
            }
        }
    }
}
