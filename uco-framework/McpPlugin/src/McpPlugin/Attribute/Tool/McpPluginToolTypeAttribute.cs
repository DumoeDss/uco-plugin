/*
┌────────────────────────────────────────────────────────────────────────┐
│  Author: Ivan Murzak (https://github.com/IvanMurzak)                   │
│  Repository: GitHub (https://github.com/IvanMurzak/MCP-Plugin-dotnet)  │
│  Copyright (c) 2025 Ivan Murzak                                        │
│  Licensed under the Apache License, Version 2.0.                       │
│  See the LICENSE file in the project root for more information.        │
└────────────────────────────────────────────────────────────────────────┘
*/

using System;

namespace com.AtelierAI.Uco.Framework
{
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class UcoToolTypeAttribute : Attribute
    {
        public string? Path { get; set; }

        public UcoToolTypeAttribute() { }
    }
}
