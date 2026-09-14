/*
┌────────────────────────────────────────────────────────────────────────┐
│  Author: Ivan Murzak (https://github.com/IvanMurzak)                   │
│  Repository: GitHub (https://github.com/IvanMurzak/MCP-Plugin-dotnet)  │
│  Copyright (c) 2025 Ivan Murzak                                        │
│  Licensed under the Apache License, Version 2.0.                       │
│  See the LICENSE file in the project root for more information.        │
└────────────────────────────────────────────────────────────────────────┘
*/

namespace com.AtelierAI.Uco.Framework.Tests.Data.Annotations
{
    [UcoToolType]
    public static class AnnotatedToolClass
    {
        [UcoTool("tool-no-hints", "Tool With No Hints")]
        public static void NoHints() { }

        [UcoTool("tool-readonly", "Read-Only Tool", ReadOnlyHint = true)]
        public static void ReadOnly() { }

        [UcoTool("tool-destructive-false", "Non-Destructive Tool", DestructiveHint = false)]
        public static void DestructiveFalse() { }

        [UcoTool("tool-idempotent", "Idempotent Tool", IdempotentHint = true)]
        public static void Idempotent() { }

        [UcoTool("tool-open-world", "Open World Tool", OpenWorldHint = true)]
        public static void OpenWorld() { }

        [UcoTool("tool-all-hints", "All Hints Tool",
            ReadOnlyHint = true,
            DestructiveHint = false,
            IdempotentHint = true,
            OpenWorldHint = false)]
        public static void AllHints() { }

        [UcoTool("tool-enabled-default", "Tool With Default Enabled")]
        public static void EnabledDefault() { }

        [UcoTool("tool-enabled-true", "Tool With Enabled True", Enabled = true)]
        public static void EnabledTrue() { }

        [UcoTool("tool-enabled-false", "Tool With Enabled False", Enabled = false)]
        public static void EnabledFalse() { }
    }
}
