/*
 * Design inspired by MCP for Unity (CoplayDev/unity-mcp), Copyright (c) Coplay Inc., MIT License.
 * Original: https://github.com/CoplayDev/unity-mcp/blob/main/Server/src/services/tools/script_apply_edits.py
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
using System.Collections.Generic;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    public static partial class Tool_Script
    {
        /// <summary>
        /// Single-pass C# source lexer that yields one entry per character with metadata
        /// distinguishing code from string/comment content, and tracking interpolation
        /// hole nesting depth.
        ///
        /// Why this exists:
        ///   - Downstream refactor / find-in-file tools need to ask "is this position
        ///     inside a string literal?" cheaply, without spinning up Roslyn.
        ///   - Regex-only replacements over C# source are dangerous: a `}` inside an
        ///     interpolation hole is real code, but a `}` outside is part of the string.
        ///   - Roslyn is heavyweight to invoke per-edit and doesn't expose a simple
        ///     "is-position-inside-a-string" query.
        ///
        /// This port preserves the behaviour of CoplayDev's `_iter_csharp_tokens` Python
        /// state machine 1:1.
        ///
        /// Covers:
        ///   - Single-line comments `// ...`
        ///   - Multi-line comments `/* ... */` (unterminated tolerated)
        ///   - Char literals `'.'` with `\` escapes
        ///   - Regular strings `"..."` with `\` escapes
        ///   - Verbatim strings `@"..."` with `""` escapes
        ///   - Interpolated strings `$"..."` with `{{` / `}}` escapes and `{` hole tracking
        ///   - Verbatim-interpolated strings `$@"..."` / `@$"..."`
        ///   - Raw string literals (C# 11) `"""..."""` (arbitrary quote count)
        ///   - Interpolated raw strings (C# 11) `$"""..."""` / `$$"""...{{ }}..."""`
        ///     (arbitrary leading `$`/quote count; hole begins at N consecutive `{`)
        ///
        /// Known limitations:
        ///   - Inside an interpolation hole we recognise nested strings/comments but do
        ///     not recursively re-enter the full state machine; e.g. a raw string nested
        ///     inside a hole is treated as a plain `"..."` string. Real-world C# rarely
        ///     does this, and the worst case is "string content is reported as code" —
        ///     not catastrophic.
        ///   - Raw string parsing tolerates a body shorter than the closing fence (i.e.
        ///     unterminated raw strings consume to EOF), matching the Python original.
        ///   - Sequences like `$$"abc"` (two `$` followed by a normal string) emit the
        ///     first `$` as code, then the second `$` is matched as `$"..."` — matches
        ///     coplaydev behaviour; such input is not valid C# anyway.
        /// </summary>
        internal static partial class CSharpTokenizer
        {
            /// <summary>
            /// One emitted token per source character. <see cref="Pos"/> is a 0-based
            /// UTF-16 code unit index into the input.
            /// </summary>
            internal readonly struct Token
            {
                public Token(int pos, char ch, bool isCode, int interpDepth)
                {
                    Pos = pos;
                    Ch = ch;
                    IsCode = isCode;
                    InterpDepth = interpDepth;
                }
                public int Pos { get; }
                public char Ch { get; }
                /// <summary>True for real syntax (outside any string/comment).</summary>
                public bool IsCode { get; }
                /// <summary>
                /// 0 outside any interpolation, &gt;0 inside the N-th nested interpolation
                /// hole. String content inside the hole still reports IsCode=false but
                /// keeps the depth populated for callers that want context.
                /// </summary>
                public int InterpDepth { get; }
            }

            /// <summary>
            /// Tiny mutable wrapper so sub-scanner iterators can advance the position.
            /// C# does not allow `ref` parameters on iterator methods, so we wrap the
            /// index in a reference type. One allocation per tokenizer invocation — cheap.
            /// </summary>
            sealed class Cursor
            {
                public int Value;
            }

            /// <summary>
            /// Streams the tokens for the entire input. Allocations are limited to the
            /// IEnumerator object and a single <see cref="Cursor"/>; each token is a struct.
            /// </summary>
            internal static IEnumerable<Token> Iterate(string text)
            {
                if (string.IsNullOrEmpty(text))
                    yield break;

                var cur = new Cursor { Value = 0 };
                int end = text.Length;

                while (cur.Value < end)
                {
                    char c = text[cur.Value];
                    char nxt = cur.Value + 1 < end ? text[cur.Value + 1] : '\0';

                    // ----- Single-line comment ----------------------------------------
                    if (c == '/' && nxt == '/')
                    {
                        yield return new Token(cur.Value, c, false, 0);
                        cur.Value++;
                        while (cur.Value < end && text[cur.Value] != '\n')
                        {
                            yield return new Token(cur.Value, text[cur.Value], false, 0);
                            cur.Value++;
                        }
                        if (cur.Value < end)
                        {
                            // The newline itself is code (separates statements).
                            yield return new Token(cur.Value, text[cur.Value], true, 0);
                            cur.Value++;
                        }
                        continue;
                    }

                    // ----- Multi-line comment -----------------------------------------
                    if (c == '/' && nxt == '*')
                    {
                        foreach (var tk in ScanMultiLineComment(text, cur))
                            yield return tk;
                        continue;
                    }

                    // ----- Interpolated raw string ($"""...""" / $$"""...""") --------
                    // Must be checked before plain $"..." and before plain """...""".
                    if (c == '$')
                    {
                        int dollarCount = 1;
                        while (cur.Value + dollarCount < end && text[cur.Value + dollarCount] == '$')
                            dollarCount++;
                        int afterDollars = cur.Value + dollarCount;
                        if (afterDollars + 2 < end
                            && text[afterDollars] == '"'
                            && text[afterDollars + 1] == '"'
                            && text[afterDollars + 2] == '"')
                        {
                            foreach (var tk in ScanInterpolatedRawString(text, cur, dollarCount))
                                yield return tk;
                            continue;
                        }
                        // Fall through to regular $"..." / $@"..." handling below.
                    }

                    // ----- Plain raw string ("""...""", non-interpolated) ------------
                    if (c == '"' && nxt == '"' && cur.Value + 2 < end && text[cur.Value + 2] == '"')
                    {
                        foreach (var tk in ScanRawString(text, cur))
                            yield return tk;
                        continue;
                    }

                    // ----- Interpolated string: $"..." / $@"..." / @$"..." -----------
                    bool isDollarQuote = c == '$' && nxt == '"';
                    bool isDollarAtQuote = c == '$' && nxt == '@' && cur.Value + 2 < end && text[cur.Value + 2] == '"';
                    bool isAtDollarQuote = c == '@' && nxt == '$' && cur.Value + 2 < end && text[cur.Value + 2] == '"';
                    if (isDollarQuote || isDollarAtQuote || isAtDollarQuote)
                    {
                        bool isVerbatim = isDollarAtQuote || isAtDollarQuote;
                        int prefixLen = isDollarQuote ? 2 : 3;
                        foreach (var tk in ScanInterpolatedString(text, cur, prefixLen, isVerbatim))
                            yield return tk;
                        continue;
                    }

                    // ----- Verbatim string: @"..." -----------------------------------
                    if (c == '@' && nxt == '"')
                    {
                        foreach (var tk in ScanVerbatimString(text, cur))
                            yield return tk;
                        continue;
                    }

                    // ----- Regular string: "..." -------------------------------------
                    if (c == '"')
                    {
                        foreach (var tk in ScanRegularString(text, cur))
                            yield return tk;
                        continue;
                    }

                    // ----- Char literal: '...' ---------------------------------------
                    if (c == '\'')
                    {
                        foreach (var tk in ScanCharLiteral(text, cur))
                            yield return tk;
                        continue;
                    }

                    // ----- Real code character ---------------------------------------
                    yield return new Token(cur.Value, c, true, 0);
                    cur.Value++;
                }
            }

            /// <summary>
            /// Returns true if the given 0-based position lies inside a string literal or
            /// a comment. O(position) — scan stops as soon as the position is reached.
            /// Suitable for one-off lookups; bulk callers should iterate once and bucket.
            /// </summary>
            internal static bool IsInStringContext(string text, int position)
            {
                if (string.IsNullOrEmpty(text) || position < 0 || position >= text.Length)
                    return false;
                foreach (var tk in Iterate(text))
                {
                    if (tk.Pos == position)
                        return !tk.IsCode;
                    if (tk.Pos > position)
                        break;
                }
                return false;
            }

            // ----------------------------------------------------------------
            // Sub-scanners. Each is itself an iterator and consumes input via
            // the shared Cursor (so position updates survive the lazy enumerator
            // initialisation gotcha that would bite a `ref int` wrapper pattern).
            // ----------------------------------------------------------------

            static IEnumerable<Token> ScanMultiLineComment(string text, Cursor cur)
            {
                int end = text.Length;
                // Emit the leading '/*'.
                yield return new Token(cur.Value, text[cur.Value], false, 0);
                cur.Value++;
                if (cur.Value >= end) yield break;
                yield return new Token(cur.Value, text[cur.Value], false, 0);
                cur.Value++;

                while (cur.Value + 1 < end)
                {
                    yield return new Token(cur.Value, text[cur.Value], false, 0);
                    if (text[cur.Value] == '*' && text[cur.Value + 1] == '/')
                    {
                        cur.Value++;
                        yield return new Token(cur.Value, text[cur.Value], false, 0);
                        cur.Value++;
                        yield break;
                    }
                    cur.Value++;
                }

                // Unterminated multi-line comment — emit the trailing char (if any) and stop.
                if (cur.Value < end)
                {
                    yield return new Token(cur.Value, text[cur.Value], false, 0);
                    cur.Value++;
                }
            }

            static IEnumerable<Token> ScanInterpolatedRawString(string text, Cursor cur, int dollarCount)
            {
                int end = text.Length;
                int afterDollars = cur.Value + dollarCount;
                // Count consecutive opening quotes (must be >= 3).
                int q = 3;
                while (afterDollars + q < end && text[afterDollars + q] == '"')
                    q++;

                // Emit the leading $...$ + """+ prefix.
                int prefixCount = dollarCount + q;
                for (int k = 0; k < prefixCount; k++)
                {
                    yield return new Token(cur.Value, text[cur.Value], false, 0);
                    cur.Value++;
                }

                int interpDepth = 0;
                while (cur.Value < end)
                {
                    char ch = text[cur.Value];
                    if (interpDepth > 0)
                    {
                        // Inside an interpolation hole — code.
                        if (ch == '{')
                        {
                            interpDepth++;
                            yield return new Token(cur.Value, ch, true, interpDepth);
                            cur.Value++;
                        }
                        else if (ch == '}')
                        {
                            yield return new Token(cur.Value, ch, true, interpDepth);
                            interpDepth--;
                            cur.Value++;
                        }
                        else if (ch == '"')
                        {
                            // Nested plain "..." string inside the hole.
                            yield return new Token(cur.Value, ch, false, interpDepth);
                            cur.Value++;
                            while (cur.Value < end)
                            {
                                yield return new Token(cur.Value, text[cur.Value], false, interpDepth);
                                if (text[cur.Value] == '\\')
                                {
                                    cur.Value++;
                                    if (cur.Value < end)
                                    {
                                        yield return new Token(cur.Value, text[cur.Value], false, interpDepth);
                                        cur.Value++;
                                    }
                                    continue;
                                }
                                if (text[cur.Value] == '"')
                                {
                                    cur.Value++;
                                    break;
                                }
                                cur.Value++;
                            }
                        }
                        else if (ch == '/' && cur.Value + 1 < end && text[cur.Value + 1] == '/')
                        {
                            yield return new Token(cur.Value, ch, false, interpDepth);
                            cur.Value++;
                            while (cur.Value < end && text[cur.Value] != '\n')
                            {
                                yield return new Token(cur.Value, text[cur.Value], false, interpDepth);
                                cur.Value++;
                            }
                        }
                        else if (ch == '/' && cur.Value + 1 < end && text[cur.Value + 1] == '*')
                        {
                            yield return new Token(cur.Value, ch, false, interpDepth);
                            cur.Value++;
                            yield return new Token(cur.Value, text[cur.Value], false, interpDepth);
                            cur.Value++;
                            while (cur.Value + 1 < end && !(text[cur.Value] == '*' && text[cur.Value + 1] == '/'))
                            {
                                yield return new Token(cur.Value, text[cur.Value], false, interpDepth);
                                cur.Value++;
                            }
                            if (cur.Value + 1 < end)
                            {
                                yield return new Token(cur.Value, text[cur.Value], false, interpDepth);
                                cur.Value++;
                                yield return new Token(cur.Value, text[cur.Value], false, interpDepth);
                                cur.Value++;
                            }
                        }
                        else
                        {
                            yield return new Token(cur.Value, ch, true, interpDepth);
                            cur.Value++;
                        }
                        continue;
                    }

                    // ---- String content (interpDepth == 0) ----
                    // Closing quote sequence?
                    if (ch == '"')
                    {
                        int qc = 1;
                        while (cur.Value + qc < end && text[cur.Value + qc] == '"')
                            qc++;
                        if (qc >= q)
                        {
                            // Emit `q` closing quotes and finish.
                            for (int k = 0; k < q; k++)
                            {
                                yield return new Token(cur.Value, text[cur.Value], false, 0);
                                cur.Value++;
                            }
                            yield break;
                        }
                        // Otherwise these are literal quotes inside the body.
                        for (int k = 0; k < qc; k++)
                        {
                            yield return new Token(cur.Value, text[cur.Value], false, 0);
                            cur.Value++;
                        }
                        continue;
                    }
                    // Opening brace sequence — interpolation hole opens at `dollarCount` consecutive '{'.
                    if (ch == '{')
                    {
                        int bc = 1;
                        while (cur.Value + bc < end && text[cur.Value + bc] == '{')
                            bc++;
                        if (bc >= dollarCount)
                        {
                            for (int k = 0; k < dollarCount; k++)
                            {
                                yield return new Token(cur.Value, text[cur.Value], true, 1);
                                cur.Value++;
                            }
                            interpDepth = 1;
                        }
                        else
                        {
                            for (int k = 0; k < bc; k++)
                            {
                                yield return new Token(cur.Value, text[cur.Value], false, 0);
                                cur.Value++;
                            }
                        }
                        continue;
                    }
                    if (ch == '}')
                    {
                        int bc = 1;
                        while (cur.Value + bc < end && text[cur.Value + bc] == '}')
                            bc++;
                        for (int k = 0; k < bc; k++)
                        {
                            yield return new Token(cur.Value, text[cur.Value], false, 0);
                            cur.Value++;
                        }
                        continue;
                    }
                    yield return new Token(cur.Value, ch, false, 0);
                    cur.Value++;
                }
            }

            static IEnumerable<Token> ScanRawString(string text, Cursor cur)
            {
                int end = text.Length;
                int q = 3;
                while (cur.Value + q < end && text[cur.Value + q] == '"')
                    q++;
                for (int k = 0; k < q; k++)
                {
                    yield return new Token(cur.Value, text[cur.Value], false, 0);
                    cur.Value++;
                }
                int closeCount = 0;
                while (cur.Value < end)
                {
                    yield return new Token(cur.Value, text[cur.Value], false, 0);
                    if (text[cur.Value] == '"')
                    {
                        closeCount++;
                        if (closeCount >= q)
                        {
                            cur.Value++;
                            yield break;
                        }
                    }
                    else
                    {
                        closeCount = 0;
                    }
                    cur.Value++;
                }
            }

            static IEnumerable<Token> ScanInterpolatedString(string text, Cursor cur, int prefixLen, bool isVerbatim)
            {
                int end = text.Length;
                for (int k = 0; k < prefixLen; k++)
                {
                    yield return new Token(cur.Value, text[cur.Value], false, 0);
                    cur.Value++;
                }
                int interpDepth = 0;
                while (cur.Value < end)
                {
                    char ch = text[cur.Value];
                    if (interpDepth > 0)
                    {
                        if (ch == '{')
                        {
                            interpDepth++;
                            yield return new Token(cur.Value, ch, true, interpDepth);
                            cur.Value++;
                        }
                        else if (ch == '}')
                        {
                            yield return new Token(cur.Value, ch, true, interpDepth);
                            interpDepth--;
                            cur.Value++;
                        }
                        else if (ch == '"')
                        {
                            // Nested string literal inside the hole.
                            yield return new Token(cur.Value, ch, false, interpDepth);
                            cur.Value++;
                            while (cur.Value < end)
                            {
                                yield return new Token(cur.Value, text[cur.Value], false, interpDepth);
                                if (text[cur.Value] == '\\')
                                {
                                    cur.Value++;
                                    if (cur.Value < end)
                                    {
                                        yield return new Token(cur.Value, text[cur.Value], false, interpDepth);
                                        cur.Value++;
                                    }
                                    continue;
                                }
                                if (text[cur.Value] == '"')
                                {
                                    cur.Value++;
                                    break;
                                }
                                cur.Value++;
                            }
                        }
                        else if (ch == '/' && cur.Value + 1 < end && text[cur.Value + 1] == '/')
                        {
                            yield return new Token(cur.Value, ch, false, interpDepth);
                            cur.Value++;
                            while (cur.Value < end && text[cur.Value] != '\n')
                            {
                                yield return new Token(cur.Value, text[cur.Value], false, interpDepth);
                                cur.Value++;
                            }
                        }
                        else if (ch == '/' && cur.Value + 1 < end && text[cur.Value + 1] == '*')
                        {
                            yield return new Token(cur.Value, ch, false, interpDepth);
                            cur.Value++;
                            yield return new Token(cur.Value, text[cur.Value], false, interpDepth);
                            cur.Value++;
                            while (cur.Value + 1 < end && !(text[cur.Value] == '*' && text[cur.Value + 1] == '/'))
                            {
                                yield return new Token(cur.Value, text[cur.Value], false, interpDepth);
                                cur.Value++;
                            }
                            if (cur.Value + 1 < end)
                            {
                                yield return new Token(cur.Value, text[cur.Value], false, interpDepth);
                                cur.Value++;
                                yield return new Token(cur.Value, text[cur.Value], false, interpDepth);
                                cur.Value++;
                            }
                        }
                        else
                        {
                            yield return new Token(cur.Value, ch, true, interpDepth);
                            cur.Value++;
                        }
                        continue;
                    }
                    // interp_depth == 0 — string content
                    if (ch == '{')
                    {
                        if (cur.Value + 1 < end && text[cur.Value + 1] == '{')
                        {
                            // Escaped '{{' — both chars are literal.
                            yield return new Token(cur.Value, ch, false, 0);
                            cur.Value++;
                            yield return new Token(cur.Value, text[cur.Value], false, 0);
                            cur.Value++;
                            continue;
                        }
                        interpDepth = 1;
                        yield return new Token(cur.Value, ch, true, interpDepth);
                        cur.Value++;
                        continue;
                    }
                    if (ch == '}')
                    {
                        if (cur.Value + 1 < end && text[cur.Value + 1] == '}')
                        {
                            yield return new Token(cur.Value, ch, false, 0);
                            cur.Value++;
                            yield return new Token(cur.Value, text[cur.Value], false, 0);
                            cur.Value++;
                            continue;
                        }
                        yield return new Token(cur.Value, ch, false, 0);
                        cur.Value++;
                        continue;
                    }
                    if (ch == '"')
                    {
                        if (isVerbatim && cur.Value + 1 < end && text[cur.Value + 1] == '"')
                        {
                            // Escaped "" inside verbatim interpolated.
                            yield return new Token(cur.Value, ch, false, 0);
                            cur.Value++;
                            yield return new Token(cur.Value, text[cur.Value], false, 0);
                            cur.Value++;
                            continue;
                        }
                        yield return new Token(cur.Value, ch, false, 0);
                        cur.Value++;
                        yield break;
                    }
                    if (!isVerbatim && ch == '\\')
                    {
                        yield return new Token(cur.Value, ch, false, 0);
                        cur.Value++;
                        if (cur.Value < end)
                        {
                            yield return new Token(cur.Value, text[cur.Value], false, 0);
                            cur.Value++;
                        }
                        continue;
                    }
                    yield return new Token(cur.Value, ch, false, 0);
                    cur.Value++;
                }
            }

            static IEnumerable<Token> ScanVerbatimString(string text, Cursor cur)
            {
                int end = text.Length;
                // Emit the leading '@' and opening '"'.
                yield return new Token(cur.Value, text[cur.Value], false, 0);
                cur.Value++;
                if (cur.Value >= end) yield break;
                yield return new Token(cur.Value, text[cur.Value], false, 0);
                cur.Value++;

                while (cur.Value < end)
                {
                    yield return new Token(cur.Value, text[cur.Value], false, 0);
                    if (text[cur.Value] == '"')
                    {
                        if (cur.Value + 1 < end && text[cur.Value + 1] == '"')
                        {
                            // Escaped '""' inside verbatim — both chars literal.
                            cur.Value++;
                            yield return new Token(cur.Value, text[cur.Value], false, 0);
                            cur.Value++;
                            continue;
                        }
                        cur.Value++;
                        yield break;
                    }
                    cur.Value++;
                }
            }

            static IEnumerable<Token> ScanRegularString(string text, Cursor cur)
            {
                int end = text.Length;
                yield return new Token(cur.Value, text[cur.Value], false, 0);
                cur.Value++;
                while (cur.Value < end)
                {
                    yield return new Token(cur.Value, text[cur.Value], false, 0);
                    if (text[cur.Value] == '\\')
                    {
                        cur.Value++;
                        if (cur.Value < end)
                        {
                            yield return new Token(cur.Value, text[cur.Value], false, 0);
                            cur.Value++;
                        }
                        continue;
                    }
                    if (text[cur.Value] == '"')
                    {
                        cur.Value++;
                        yield break;
                    }
                    cur.Value++;
                }
            }

            static IEnumerable<Token> ScanCharLiteral(string text, Cursor cur)
            {
                int end = text.Length;
                yield return new Token(cur.Value, text[cur.Value], false, 0);
                cur.Value++;
                while (cur.Value < end)
                {
                    yield return new Token(cur.Value, text[cur.Value], false, 0);
                    if (text[cur.Value] == '\\')
                    {
                        cur.Value++;
                        if (cur.Value < end)
                        {
                            yield return new Token(cur.Value, text[cur.Value], false, 0);
                            cur.Value++;
                        }
                        continue;
                    }
                    if (text[cur.Value] == '\'')
                    {
                        cur.Value++;
                        yield break;
                    }
                    cur.Value++;
                }
            }
        }
    }
}
