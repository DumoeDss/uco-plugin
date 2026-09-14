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
    [UcoPromptType]
    public static class AnnotatedPromptClass
    {
        [UcoPrompt(Name = "prompt-enabled-default")]
        public static string EnabledDefault() => "default";

        [UcoPrompt(Name = "prompt-enabled-true", Enabled = true)]
        public static string EnabledTrue() => "enabled";

        [UcoPrompt(Name = "prompt-enabled-false", Enabled = false)]
        public static string EnabledFalse() => "disabled";
    }
}
