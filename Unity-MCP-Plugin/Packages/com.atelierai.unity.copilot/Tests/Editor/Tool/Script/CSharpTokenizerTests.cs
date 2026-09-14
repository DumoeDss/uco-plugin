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
using System.Collections.Generic;
using System.Linq;
using com.AtelierAI.Unity.Copilot.Editor.API;
using NUnit.Framework;

namespace com.AtelierAI.Unity.Copilot.Editor.Tests
{
    /// <summary>
    /// Unit-level coverage for the C# tokenizer used by <c>script-apply-edits</c>.
    /// Visible thanks to <c>InternalsVisibleTo("com.AtelierAI.Unity.Copilot.Editor.Tests")</c>
    /// declared in the Editor assembly's <c>AssemblyInfo.cs</c>.
    ///
    /// Each test runs <c>Tool_Script.CSharpTokenizer.IsInStringContext</c> at a hand-picked
    /// position and asserts that the classification (code vs string/comment) matches the
    /// intended C# semantics for that position.
    /// </summary>
    public class CSharpTokenizerTests
    {
        // Helper for compact assertion lines.
        static bool InString(string text, int pos) => Tool_Script.CSharpTokenizer.IsInStringContext(text, pos);

        // -----------------------------------------------------------------
        // Single-line and multi-line comments
        // -----------------------------------------------------------------

        [Test]
        public void SingleLineComment_AllCharsAreNonCode_NewlineIsCode()
        {
            //         0123456789012345
            var text = "int x; // hello\nint y;";
            Assert.IsFalse(InString(text, 0), "first 'int' must be code");
            Assert.IsTrue(InString(text, 7), "'/' of '//' is in comment");
            Assert.IsTrue(InString(text, 11), "'h' of 'hello' is in comment");
            // Newline at index 15 — per the Python original, the terminating newline IS code.
            Assert.IsFalse(InString(text, 15), "newline terminating // is code");
            Assert.IsFalse(InString(text, 16), "'i' of next statement is code");
        }

        [Test]
        public void MultiLineComment_ClassifiedAsNonCode()
        {
            //         0          1
            //         0123456789012345
            var text = "int /* x */ y;";
            Assert.IsTrue(InString(text, 4), "'/' opening /*");
            Assert.IsTrue(InString(text, 8), "'x' inside /* */");
            Assert.IsTrue(InString(text, 10), "'/' closing */");
            Assert.IsFalse(InString(text, 12), "'y' after closing comment is code");
        }

        // -----------------------------------------------------------------
        // Char literal and regular string
        // -----------------------------------------------------------------

        [Test]
        public void CharLiteral_BodyIsNonCode()
        {
            var text = "char c = 'a';";
            int aIdx = text.IndexOf('a');
            Assert.IsTrue(InString(text, aIdx), "'a' inside char literal");
            Assert.IsFalse(InString(text, text.IndexOf(';')), "';' after char literal");
        }

        [Test]
        public void RegularString_EscapeQuoteDoesNotClose()
        {
            // The escaped \" must NOT end the string; the 'X' after it stays inside the string.
            var text = "string s = \"he said \\\"hi\\\" X\";";
            int xIdx = text.IndexOf('X');
            int semiIdx = text.IndexOf(';');
            Assert.IsTrue(InString(text, xIdx), "'X' is still inside the string after escaped quotes");
            Assert.IsFalse(InString(text, semiIdx), "';' is code outside the string");
        }

        // -----------------------------------------------------------------
        // Verbatim string @"..."
        // -----------------------------------------------------------------

        [Test]
        public void VerbatimString_DoubleQuoteEscapesQuoteAndStaysOpen()
        {
            // @"a""b" — the embedded "" is an escaped quote, not a close.
            var text = "var s = @\"a\"\"b\";";
            int bIdx = text.IndexOf('b');
            Assert.IsTrue(InString(text, bIdx), "'b' remains inside the verbatim string");
        }

        // -----------------------------------------------------------------
        // Interpolated strings — the high-value case
        // -----------------------------------------------------------------

        [Test]
        public void InterpolatedString_BraceInHoleIsCode_BraceOutsideIsString()
        {
            // $"hello {name} world"
            //         ^ position of '{'
            var text = "var s = $\"hello {name} world\";";
            int openBraceIdx = text.IndexOf('{');
            int nameIdx = text.IndexOf("name");
            int closeBraceIdx = text.IndexOf('}');
            int worldWIdx = text.IndexOf('w', closeBraceIdx);

            Assert.IsFalse(InString(text, openBraceIdx),
                "the '{' opening an interpolation hole is CODE");
            Assert.IsFalse(InString(text, nameIdx),
                "identifier inside the hole is CODE");
            Assert.IsFalse(InString(text, closeBraceIdx),
                "the '}' closing the hole is CODE");
            Assert.IsTrue(InString(text, worldWIdx),
                "'w' of 'world' is STRING content");
        }

        [Test]
        public void InterpolatedString_EscapedBracesAreLiteralString()
        {
            // $"{{ literal }}"
            var text = "var s = $\"{{ literal }}\";";
            int firstOpenIdx = text.IndexOf('{');
            int litIdx = text.IndexOf("literal");
            Assert.IsTrue(InString(text, firstOpenIdx), "'{{' is a literal '{' — string content, not code");
            Assert.IsTrue(InString(text, litIdx), "'literal' is plain string content");
        }

        [Test]
        public void VerbatimInterpolated_BraceInHoleIsCode()
        {
            // $@"\path {x} \more"
            var text = "var s = $@\"\\path {x} \\more\";";
            int xIdx = text.IndexOf('x');
            Assert.IsFalse(InString(text, xIdx), "identifier in verbatim-interpolated hole is code");
        }

        // -----------------------------------------------------------------
        // Raw string literals (C# 11)
        // -----------------------------------------------------------------

        [Test]
        public void RawString_TripleQuoted_BodyIsNonCode()
        {
            // """abc"""
            // Note: Roslyn requires the body to not include 3 consecutive quotes; "abc" is fine.
            var text = "var s = \"\"\"abc\"\"\";";
            int aIdx = text.IndexOf('a');
            Assert.IsTrue(InString(text, aIdx), "'a' inside raw string is non-code");
        }

        [Test]
        public void InterpolatedRawString_BraceCountSwitchesToCode()
        {
            // $$"""...{{ expr }}..."""
            // Two dollar signs require TWO consecutive '{' to open a hole.
            var text = "var s = $$\"\"\"abc {{ expr }} xyz\"\"\";";
            int exprIdx = text.IndexOf("expr");
            Assert.IsFalse(InString(text, exprIdx),
                "'expr' inside a raw-interpolation hole opened by '{{' is CODE");
            int xyzIdx = text.IndexOf("xyz");
            Assert.IsTrue(InString(text, xyzIdx), "'xyz' after the closing '}}' is string content");
        }

        // -----------------------------------------------------------------
        // Iterate-level checks (ensures positions stay in lock-step with input)
        // -----------------------------------------------------------------

        [Test]
        public void Iterate_EmitsOneTokenPerCharacterInOrder()
        {
            var text = "abc \"de\" fg";
            var tokens = Tool_Script.CSharpTokenizer.Iterate(text).ToList();
            Assert.AreEqual(text.Length, tokens.Count, "one token per character");
            for (int i = 0; i < text.Length; i++)
            {
                Assert.AreEqual(i, tokens[i].Pos, $"position at index {i}");
                Assert.AreEqual(text[i], tokens[i].Ch, $"char at index {i}");
            }
        }

        [Test]
        public void Iterate_TracksInterpolationDepth()
        {
            // $"{a + $"{b}" }" — outer hole has nested string with another hole.
            // We don't need to exhaustively verify nested depths here; just confirm that
            // the depth never goes negative and reaches >= 1 inside the outer hole.
            var text = "var s = $\"{a}\";";
            var tokens = Tool_Script.CSharpTokenizer.Iterate(text).ToList();
            Assert.IsTrue(tokens.Any(t => t.InterpDepth >= 1),
                "at least one token should report an interpolation depth >= 1");
            Assert.IsFalse(tokens.Any(t => t.InterpDepth < 0),
                "interpolation depth must never go negative");
        }

        [Test]
        public void Iterate_EmptyInputEmitsNoTokens()
        {
            Assert.AreEqual(0, Tool_Script.CSharpTokenizer.Iterate(string.Empty).Count());
        }

        [Test]
        public void Iterate_UnterminatedMultiLineCommentDoesNotThrow()
        {
            // Pathological input — make sure the tokenizer halts gracefully.
            var text = "int x; /* never closes";
            var tokens = Tool_Script.CSharpTokenizer.Iterate(text).ToList();
            Assert.AreEqual(text.Length, tokens.Count, "one token per character even for unterminated input");
        }
    }
}
