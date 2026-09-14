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
using System.IO;
using System.Text;
using com.AtelierAI.Unity.Copilot.Editor.API;
using NUnit.Framework;
using UnityEditor;

namespace com.AtelierAI.Unity.Copilot.Editor.Tests
{
    /// <summary>
    /// Behavioural coverage for <c>script-apply-edits</c> + <c>script-get-sha</c>.
    /// Each test creates a temp `.cs` file under <c>Assets/</c>, exercises the tool,
    /// then cleans up the artefact (and its .meta). The Unity AssetDatabase refresh is
    /// part of the tool surface — these tests run synchronously because <c>ApplyEdits</c>
    /// dispatches to the main thread internally and waits.
    /// </summary>
    public class ScriptApplyEditsTests
    {
        // Cluster temp files under a single sub-folder so a stray test failure leaves a
        // single dir to GC rather than scattered .cs files at the Assets root.
        const string TempFolderRel = "Assets/_ScriptApplyEditsTests_Temp";
        const string TempFolderAbs = "Assets/_ScriptApplyEditsTests_Temp";

        [SetUp]
        public void EnsureTempFolder()
        {
            if (!Directory.Exists(TempFolderAbs))
                Directory.CreateDirectory(TempFolderAbs);
        }

        [TearDown]
        public void RemoveTempFolder()
        {
            try
            {
                if (Directory.Exists(TempFolderAbs))
                {
                    Directory.Delete(TempFolderAbs, recursive: true);
                    var meta = TempFolderAbs + ".meta";
                    if (File.Exists(meta)) File.Delete(meta);
                }
                AssetDatabase.Refresh();
            }
            catch (Exception)
            {
                // Don't fail teardown — Unity may have a transient lock on the meta file.
            }
        }

        static string WriteFile(string name, string content)
        {
            var path = TempFolderRel + "/" + name;
            File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return path;
        }

        // ---------------------------------------------------------------------
        // GetSha
        // ---------------------------------------------------------------------

        [Test]
        public void GetSha_ReturnsConsistentHashForSameContent()
        {
            var path = WriteFile("ShaA.cs", "class A { }\n");
            var a = Tool_Script.GetSha(path);
            var b = Tool_Script.GetSha(path);
            Assert.IsTrue(a.Ok, a.Error);
            Assert.IsTrue(b.Ok, b.Error);
            Assert.AreEqual(a.Sha, b.Sha);
            Assert.AreEqual("class A { }\n".Length, a.ByteSize);
            // Length should be exactly 64 hex chars for SHA-256.
            Assert.AreEqual(64, a.Sha!.Length);
        }

        [Test]
        public void GetSha_FailsOnMissingFile()
        {
            var r = Tool_Script.GetSha(TempFolderRel + "/DoesNotExist.cs");
            Assert.IsFalse(r.Ok);
            Assert.IsNotNull(r.Error);
        }

        [Test]
        public void GetSha_FailsOnWrongExtension()
        {
            var r = Tool_Script.GetSha(TempFolderRel + "/foo.txt");
            Assert.IsFalse(r.Ok);
        }

        // ---------------------------------------------------------------------
        // ApplyEdits — happy paths
        // ---------------------------------------------------------------------

        [Test]
        public void ApplyEdits_SimpleReplacement_WorksAndUpdatesSha()
        {
            var path = WriteFile("Simple.cs",
                "public class Simple\n" +
                "{\n" +
                "    public int X = 1;\n" +
                "}\n");
            var before = Tool_Script.GetSha(path).Sha;
            // Replace "1" with "42" on line index 2 (0-based), at column 19..20.
            // "    public int X = 1;" — columns: 0..3 spaces, 4..9 "public", 10 space,
            // 11..13 "int", 14 space, 15 'X', 16 space, 17 '=', 18 space, 19 '1', 20 ';'.
            var edits = new[]
            {
                new Tool_Script.TextEdit
                {
                    Range = new Tool_Script.Range { StartLine = 2, StartColumn = 19, EndLine = 2, EndColumn = 20 },
                    NewText = "42",
                },
            };
            var result = Tool_Script.ApplyEdits(path, edits, expectedSha: before);
            Assert.IsTrue(result.Ok, result.Error + ": " + result.ErrorDetail);
            var written = File.ReadAllText(path);
            StringAssert.Contains("X = 42;", written);
            Assert.AreNotEqual(before, result.CurrentSha);
        }

        [Test]
        public void ApplyEdits_PureInsertionAtEndOfFile()
        {
            var path = WriteFile("Insert.cs", "class X { }\n");
            var edits = new[]
            {
                new Tool_Script.TextEdit
                {
                    // Insert at the start of the virtual EOF line (line index 1, col 0).
                    Range = new Tool_Script.Range { StartLine = 1, StartColumn = 0, EndLine = 1, EndColumn = 0 },
                    NewText = "class Y { }\n",
                },
            };
            var result = Tool_Script.ApplyEdits(path, edits);
            Assert.IsTrue(result.Ok, result.Error + ": " + result.ErrorDetail);
            var content = File.ReadAllText(path);
            Assert.AreEqual("class X { }\nclass Y { }\n", content);
        }

        [Test]
        public void ApplyEdits_MultipleEdits_AppliedAtomically_NoOffsetShift()
        {
            // The post-edit text must stay valid C# — ApplyEdits runs a Roslyn syntax
            // check before writing, so bare identifiers (the old "AAA"→"ZZ" fixture)
            // are correctly rejected with SYNTAX_ERROR.
            var path = WriteFile("Multi.cs",
                "int AAA = 1;\n" +
                "int BBB = 2;\n" +
                "int CCC = 3;\n");
            // Two non-overlapping replacements; provided in *forward* order to prove the
            // tool re-orders them for us. First is shorter, second is longer than the
            // replaced span — later offsets must not shift.
            var edits = new[]
            {
                new Tool_Script.TextEdit
                {
                    Range = new Tool_Script.Range { StartLine = 0, StartColumn = 4, EndLine = 0, EndColumn = 7 },
                    NewText = "Z",       // shorter
                },
                new Tool_Script.TextEdit
                {
                    Range = new Tool_Script.Range { StartLine = 2, StartColumn = 4, EndLine = 2, EndColumn = 7 },
                    NewText = "WWWWWW", // longer
                },
            };
            var result = Tool_Script.ApplyEdits(path, edits);
            Assert.IsTrue(result.Ok, result.Error + ": " + result.ErrorDetail);
            Assert.AreEqual("int Z = 1;\nint BBB = 2;\nint WWWWWW = 3;\n", File.ReadAllText(path));
        }

        [Test]
        public void ApplyEdits_PreservesCRLFLineEndings()
        {
            var path = TempFolderRel + "/Crlf.cs";
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes("class X\r\n{\r\n}\r\n"));
            var edits = new[]
            {
                new Tool_Script.TextEdit
                {
                    // Replace "X" with "Y" on line 0, col 6..7
                    Range = new Tool_Script.Range { StartLine = 0, StartColumn = 6, EndLine = 0, EndColumn = 7 },
                    NewText = "Y",
                },
            };
            var result = Tool_Script.ApplyEdits(path, edits);
            Assert.IsTrue(result.Ok, result.Error + ": " + result.ErrorDetail);
            var bytes = File.ReadAllBytes(path);
            // Original CRLFs preserved — should still have \r\n at positions 7-8 and onwards.
            var asString = Encoding.UTF8.GetString(bytes);
            Assert.AreEqual("class Y\r\n{\r\n}\r\n", asString);
        }

        // ---------------------------------------------------------------------
        // ApplyEdits — error paths
        // ---------------------------------------------------------------------

        [Test]
        public void ApplyEdits_FailsWithStaleSha()
        {
            var path = WriteFile("Stale.cs", "class S { }\n");
            var staleSha = "0000000000000000000000000000000000000000000000000000000000000000";
            var edits = new[]
            {
                new Tool_Script.TextEdit
                {
                    Range = new Tool_Script.Range { StartLine = 0, StartColumn = 0, EndLine = 0, EndColumn = 5 },
                    NewText = "struct",
                },
            };
            var result = Tool_Script.ApplyEdits(path, edits, expectedSha: staleSha);
            Assert.IsFalse(result.Ok);
            Assert.AreEqual("STALE_SHA", result.Error);
            Assert.IsNotNull(result.CurrentSha);
            Assert.IsNotNull(result.CurrentContent);
            // File unchanged on disk.
            Assert.AreEqual("class S { }\n", File.ReadAllText(path));
        }

        [Test]
        public void ApplyEdits_FailsWithSyntaxErrorAndDoesNotWrite()
        {
            var path = WriteFile("Syntax.cs", "class S { public int X = 1; }\n");
            var before = File.ReadAllText(path);
            var edits = new[]
            {
                new Tool_Script.TextEdit
                {
                    // Replace `int X = 1;` with garbage that won't parse
                    Range = new Tool_Script.Range { StartLine = 0, StartColumn = 10, EndLine = 0, EndColumn = 27 },
                    NewText = "@@@",
                },
            };
            var result = Tool_Script.ApplyEdits(path, edits, validate: true);
            Assert.IsFalse(result.Ok);
            Assert.AreEqual("SYNTAX_ERROR", result.Error);
            Assert.IsTrue(result.Diagnostics.Length > 0);
            // File untouched.
            Assert.AreEqual(before, File.ReadAllText(path));
        }

        [Test]
        public void ApplyEdits_FailsWithOverlappingRanges()
        {
            var path = WriteFile("Overlap.cs", "0123456789\n");
            var edits = new[]
            {
                new Tool_Script.TextEdit
                {
                    Range = new Tool_Script.Range { StartLine = 0, StartColumn = 1, EndLine = 0, EndColumn = 5 },
                    NewText = "X",
                },
                new Tool_Script.TextEdit
                {
                    Range = new Tool_Script.Range { StartLine = 0, StartColumn = 3, EndLine = 0, EndColumn = 7 },
                    NewText = "Y",
                },
            };
            var result = Tool_Script.ApplyEdits(path, edits);
            Assert.IsFalse(result.Ok);
            Assert.AreEqual("INVALID_RANGE", result.Error);
        }

        [Test]
        public void ApplyEdits_FailsOnOutOfBoundsLine()
        {
            var path = WriteFile("Oob.cs", "class O { }\n");
            var edits = new[]
            {
                new Tool_Script.TextEdit
                {
                    Range = new Tool_Script.Range { StartLine = 999, StartColumn = 0, EndLine = 999, EndColumn = 0 },
                    NewText = "x",
                },
            };
            var result = Tool_Script.ApplyEdits(path, edits);
            Assert.IsFalse(result.Ok);
            Assert.AreEqual("INVALID_RANGE", result.Error);
        }

        [Test]
        public void ApplyEdits_FailsOnPathOutsideAssetsAndPackages()
        {
            var edits = new[]
            {
                new Tool_Script.TextEdit
                {
                    Range = new Tool_Script.Range { StartLine = 0, StartColumn = 0, EndLine = 0, EndColumn = 0 },
                    NewText = "x",
                },
            };
            var result = Tool_Script.ApplyEdits("Library/foo.cs", edits);
            Assert.IsFalse(result.Ok);
            Assert.AreEqual("INVALID_PATH", result.Error);
        }

        [Test]
        public void ApplyEdits_FailsOnEmptyEditsArray()
        {
            var path = WriteFile("Empty.cs", "class E { }\n");
            var result = Tool_Script.ApplyEdits(path, Array.Empty<Tool_Script.TextEdit>());
            Assert.IsFalse(result.Ok);
            Assert.AreEqual("NO_EDITS", result.Error);
        }

        // ---------------------------------------------------------------------
        // Tokenizer-affected behaviour — brace inside interpolated string
        // ---------------------------------------------------------------------
        // We don't test the tokenizer directly (it's internal and lives in a different
        // asmdef), but we *do* test that a "syntactically suspicious" file with braces
        // inside interpolated strings parses successfully when written by ApplyEdits —
        // which proves the syntax-validate pass is intact for these inputs.

        [Test]
        public void ApplyEdits_SyntaxValidation_TolneratesInterpolatedStringBraces()
        {
            var path = WriteFile("Interp.cs",
                "class I {\n" +
                "    public string F(int n) => $\"hello {n} world\";\n" +
                "}\n");
            // Trivial no-op edit replacing "hello" with "hi" on line 1, col 35..40.
            // Line: `    public string F(int n) => $"hello {n} world";`
            //                                       ^col 35
            var line = "    public string F(int n) => $\"hello {n} world\";";
            int idx = line.IndexOf("hello", StringComparison.Ordinal);
            var edits = new[]
            {
                new Tool_Script.TextEdit
                {
                    Range = new Tool_Script.Range { StartLine = 1, StartColumn = idx, EndLine = 1, EndColumn = idx + "hello".Length },
                    NewText = "hi",
                },
            };
            var result = Tool_Script.ApplyEdits(path, edits, validate: true);
            Assert.IsTrue(result.Ok, result.Error + ": " + result.ErrorDetail);
            StringAssert.Contains("$\"hi {n} world\"", File.ReadAllText(path));
        }
    }
}
