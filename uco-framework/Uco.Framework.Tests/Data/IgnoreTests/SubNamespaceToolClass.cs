/*
┌────────────────────────────────────────────────────────────────────────┐
│  Author: Ivan Murzak (https://github.com/IvanMurzak)                   │
│  Repository: GitHub (https://github.com/IvanMurzak/MCP-Plugin-dotnet)  │
│  Copyright (c) 2025 Ivan Murzak                                        │
│  Licensed under the Apache License, Version 2.0.                       │
│  See the LICENSE file in the project root for more information.        │
└────────────────────────────────────────────────────────────────────────┘
*/

namespace com.AtelierAI.Uco.Framework.Tests.Data.Ignored.SubNamespace
{
    [UcoToolType]
    internal class SubNamespaceToolClass
    {
        [UcoTool("sub-namespace-tool", "Tool in sub-namespace")]
        public static string TestTool() => "test";
    }

    [UcoPromptType]
    internal class SubNamespacePromptClass
    {
        [UcoPrompt(Name = "sub-namespace-prompt")]
        public static string TestPrompt() => "test prompt";
    }
}
