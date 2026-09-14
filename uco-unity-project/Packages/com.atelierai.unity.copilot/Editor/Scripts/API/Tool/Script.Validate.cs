/*
 * Design inspired by MCP for Unity (CoplayDev/unity-mcp), Copyright (c) Coplay Inc., MIT License.
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
using com.AtelierAI.Uco.Framework;
using com.IvanMurzak.ReflectorNet.Utils;
using com.AtelierAI.Unity.Copilot.Utils;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RoslynDiagnostic = Microsoft.CodeAnalysis.Diagnostic;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    public static partial class Tool_Script
    {
        /// <summary>
        /// Validation strictness for <see cref="Validate"/>.
        /// </summary>
        public enum ValidationLevel
        {
            /// <summary>Syntax-only: parser checks, no compilation. Fastest, no project references resolved.</summary>
            Syntax = 0,
            /// <summary>Syntax + semantic: type resolution against project assemblies (catches unknown types/methods/namespaces).</summary>
            Semantic = 1,
            /// <summary>Strict: Semantic + warnings-as-errors + nullable strictness. Catches latent issues but can be noisy.</summary>
            Strict = 2,
        }

        /// <summary>
        /// Diagnostic entry returned by <see cref="Validate"/>.
        /// </summary>
        public class ValidationDiagnostic
        {
            public string Severity { get; set; } = string.Empty; // "Error" | "Warning" | "Info" | "Hidden"
            public string Code { get; set; } = string.Empty;     // e.g. "CS1002"
            public string Message { get; set; } = string.Empty;
            public int Line { get; set; }       // 1-based
            public int Column { get; set; }     // 1-based
            public int EndLine { get; set; }    // 1-based
            public int EndColumn { get; set; }  // 1-based
        }

        /// <summary>
        /// Aggregated result returned by <see cref="Validate"/>.
        /// </summary>
        public class ValidationResult
        {
            public bool Ok { get; set; }
            public ValidationLevel Level { get; set; }
            public int ErrorCount { get; set; }
            public int WarningCount { get; set; }
            public ValidationDiagnostic[] Diagnostics { get; set; } = Array.Empty<ValidationDiagnostic>();
        }

        public const string ScriptValidateToolId = "script-validate";

        // Hard cap on input size to avoid OOM from runaway inputs. 1 MiB of C# source is already huge.
        private const int MaxValidateCodeBytes = 1 * 1024 * 1024;

        [UcoTool
        (
            ScriptValidateToolId,
            Title = "Script / Validate",
            ReadOnlyHint = true,
            IdempotentHint = true
        )]
        [UcoSkillDescription("Validate C# source with Roslyn at three strictness levels (Syntax / Semantic / Strict). " +
            "Does NOT execute or write to disk — safe pre-flight lint before '" + ScriptUpdateOrCreateToolId + "'. " +
            "Strict / Semantic catch references to unknown namespaces, types, and methods against the project's loaded assemblies.")]
        [UcoSkillBody("Validates a C# source string with Roslyn and returns structured diagnostics. " +
            "Pure read-only — does NOT compile to an assembly, write any file, or invoke any code. " +
            "Pair with '" + ScriptUpdateOrCreateToolId + "' as a pre-flight check before writing scripts to disk.\n\n" +
            "## Inputs\n\n" +
            "- `code` — required C# source text (the entire file contents). Throws if empty or larger than 1 MiB.\n" +
            "- `level` — validation strictness:\n" +
            "  - `Syntax` (default) — parser-only, fastest. Catches malformed C# (`CS1002` missing `;`, `CS1513` unmatched braces, etc.).\n" +
            "  - `Semantic` — parser + symbol resolution against all loaded project assemblies. Catches `CS0246` (unknown type), `CS0103` (name not in scope), `CS1061` (no such member), unknown namespaces.\n" +
            "  - `Strict` — Semantic + warnings-as-errors + nullable context enabled. Useful but can be noisy on legacy code.\n" +
            "- `fileName` — optional display name shown in `Diagnostic.Code` paths. Defaults to `Submitted.cs`.\n\n" +
            "## Output\n\n" +
            "Returns a `ValidationResult`:\n" +
            "- `Ok` — true iff zero diagnostics with `Severity == Error`.\n" +
            "- `Level` — the level that was actually run.\n" +
            "- `ErrorCount` / `WarningCount` — totals.\n" +
            "- `Diagnostics[]` — each with `Severity`, `Code` (e.g. `CS0246`), `Message`, and 1-based `Line`/`Column`/`EndLine`/`EndColumn`.\n\n" +
            "## Notes\n\n" +
            "- `Semantic` and `Strict` reuse the assembly reference set used by '" + ScriptExecuteToolId + "' " +
            "  (all non-dynamic assemblies in the current AppDomain with a non-empty `Location`).\n" +
            "- Semantic resolution covers anything visible to your project — Unity APIs, packages, your own scripts " +
            "  that compiled successfully. If your project has compile errors, those types won't be resolvable here either.\n" +
            "- This tool never runs the code. For execution use '" + ScriptExecuteToolId + "'.")]
        [Description("Validates C# source via Roslyn at Syntax / Semantic / Strict levels without compiling to an assembly or " +
            "writing any file. Use as a pre-flight check before '" + ScriptUpdateOrCreateToolId + "'. Semantic/Strict catch " +
            "references to unknown namespaces, types, and methods against the project's loaded assemblies.")]
        public static ValidationResult Validate
        (
            [Description("C# source code to validate (full file text).")]
            string code,
            [Description("Validation strictness. 'Syntax' (default) = parser only. 'Semantic' = parser + project-assembly symbol " +
                "resolution (catches unknown types/methods/namespaces). 'Strict' = Semantic + warnings-as-errors + nullable.")]
            ValidationLevel level = ValidationLevel.Syntax,
            [Description("Optional file name shown in diagnostic locations. Defaults to 'Submitted.cs'.")]
            string? fileName = null
        )
        {
            if (string.IsNullOrEmpty(code))
                throw new ArgumentException($"'{nameof(code)}' is null or empty. Please provide C# source to validate.", nameof(code));

            // Defensive size cap. Roslyn handles large inputs but we don't want runaway memory from malformed clients.
            // 2 bytes per char overestimates UTF-16 but is the right thing to compare against `byte` budget.
            if ((long)code.Length * 2 > MaxValidateCodeBytes)
                throw new ArgumentException(
                    $"'{nameof(code)}' is too large ({code.Length} chars). Limit: {MaxValidateCodeBytes / 1024} KiB.",
                    nameof(code));

            var effectiveFileName = string.IsNullOrWhiteSpace(fileName) ? "Submitted.cs" : fileName!;

            try
            {
                var syntaxTree = CSharpSyntaxTree.ParseText(code, path: effectiveFileName);

                IEnumerable<RoslynDiagnostic> diagnostics;

                if (level == ValidationLevel.Syntax)
                {
                    diagnostics = syntaxTree.GetDiagnostics();
                }
                else
                {
                    // Reused from Script.Execute.cs: aggregate non-dynamic AppDomain assemblies as MetadataReferences.
                    // Roslyn parsing/compilation is pure CPU and does NOT require Unity main thread.
                    var references = BuildProjectReferences();

                    var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                        .WithWarningLevel(4);

                    if (level == ValidationLevel.Strict)
                    {
                        options = options
                            .WithGeneralDiagnosticOption(ReportDiagnostic.Error)
                            .WithNullableContextOptions(NullableContextOptions.Enable);
                    }

                    var compilation = CSharpCompilation.Create(
                        assemblyName: "ValidationAssembly",
                        syntaxTrees: new[] { syntaxTree },
                        references: references,
                        options: options
                    );

                    // GetDiagnostics runs parse + declaration + method-body binding without emitting an assembly.
                    diagnostics = compilation.GetDiagnostics();
                }

                var diagList = diagnostics.ToList();

                var errorCount = diagList.Count(d => d.Severity == DiagnosticSeverity.Error);
                var warningCount = diagList.Count(d => d.Severity == DiagnosticSeverity.Warning);

                return new ValidationResult
                {
                    Ok = errorCount == 0,
                    Level = level,
                    ErrorCount = errorCount,
                    WarningCount = warningCount,
                    Diagnostics = diagList.Select(MapDiagnostic).ToArray(),
                };
            }
            catch (ArgumentException)
            {
                // Preserve clean caller errors (size/empty checks above bubble through unchanged).
                throw;
            }
            catch (Exception ex)
            {
                // Roslyn occasionally throws on pathological inputs; surface as a single synthetic error rather than 500ing.
                return new ValidationResult
                {
                    Ok = false,
                    Level = level,
                    ErrorCount = 1,
                    WarningCount = 0,
                    Diagnostics = new[]
                    {
                        new ValidationDiagnostic
                        {
                            Severity = "Error",
                            Code = "MCP_VALIDATE_INTERNAL",
                            Message = $"Validation failed internally: {ex.GetType().Name}: {ex.Message}",
                            Line = 1, Column = 1, EndLine = 1, EndColumn = 1,
                        }
                    },
                };
            }
        }

        /// <summary>
        /// Reused from Script.Execute.cs (ExecuteCSharpCode): build a MetadataReference[] from every loaded,
        /// non-dynamic AppDomain assembly with a resolvable on-disk Location. Silently skips assemblies whose
        /// files have moved or been unloaded — same forgiving behavior as the executor.
        /// </summary>
        static List<MetadataReference> BuildProjectReferences()
        {
            var result = new List<MetadataReference>();
            foreach (var assembly in AssemblyUtils.AllAssemblies)
            {
                if (assembly.IsDynamic) continue;
                if (string.IsNullOrEmpty(assembly.Location)) continue;
                try
                {
                    result.Add(MetadataReference.CreateFromFile(assembly.Location));
                }
                catch (DirectoryNotFoundException) { /* assembly unloaded / moved — skip */ }
                catch (FileNotFoundException) { /* assembly unloaded / moved — skip */ }
                catch (Exception) { /* defensive — never let a single bad assembly kill validation */ }
            }
            return result;
        }

        static ValidationDiagnostic MapDiagnostic(RoslynDiagnostic d)
        {
            // Roslyn FileLinePositionSpan is 0-based; we surface 1-based to match human/IDE expectations.
            var span = d.Location.GetLineSpan();
            return new ValidationDiagnostic
            {
                Severity = d.Severity.ToString(),
                Code = d.Id,
                Message = d.GetMessage(),
                Line = span.StartLinePosition.Line + 1,
                Column = span.StartLinePosition.Character + 1,
                EndLine = span.EndLinePosition.Line + 1,
                EndColumn = span.EndLinePosition.Character + 1,
            };
        }
    }
}
