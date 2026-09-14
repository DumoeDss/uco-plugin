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
    public static class MixedToolTypeClass
    {
        [UcoTool("standard-tool-a", "Standard Tool A")]
        public static string StandardA() => "a";

        [UcoTool("standard-tool-b", "Standard Tool B")]
        public static string StandardB() => "b";

        [UcoTool(
            "system-tool-x",
            "System Tool X",
            ToolType = UcoToolType.System,
            ReadOnlyHint = true,
            DestructiveHint = false,
            IdempotentHint = true,
            OpenWorldHint = false)]
        public static string SystemX() => "x";

        [UcoTool("system-tool-y", "System Tool Y", ToolType = UcoToolType.System)]
        public static string SystemY() => "y";

        [UcoTool("standard-default", "Default ToolType")]
        public static string DefaultType() => "default";
    }
}
