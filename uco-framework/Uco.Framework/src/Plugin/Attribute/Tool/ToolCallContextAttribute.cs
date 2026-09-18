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
    /// <summary>
    /// Marks a reflected tool parameter that receives the current invocation
    /// context.  This is an internal dispatch seam and is omitted from the
    /// generated tool input schema.
    /// </summary>
    [AttributeUsage(AttributeTargets.Parameter)]
    public sealed class ToolCallContextAttribute : Attribute
    {
    }
}
