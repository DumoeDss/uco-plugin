/*
┌──────────────────────────────────────────────────────────────────┐
│  Author: Ivan Murzak (https://github.com/IvanMurzak/Unity-MCP)    │
│  Repository: GitHub (https://github.com/IvanMurzak/Unity-MCP)     │
│  Copyright (c) 2025 Ivan Murzak                                   │
│  Licensed under the Apache License, Version 2.0.                  │
│  See the LICENSE file in the project root for more information.   │
└──────────────────────────────────────────────────────────────────┘
*/

#nullable enable
using System;
using System.ComponentModel;
using com.IvanMurzak.ReflectorNet.Model;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    /// <summary>
    /// script-execute result envelope: the serialized return value plus the
    /// observed scene-mutation flag and, when sandboxed, the restoration
    /// outcome (COCli-06).
    /// </summary>
    [Description("Result of a dynamic C# script execution with scene-hygiene reporting.")]
    public sealed class ScriptExecuteResult
    {
        [Description("The serialized return value of the executed method (null for void).")]
        public SerializedMember? Value { get; set; }

        [Description("True when execution left any open scene dirty or changed the active scene or selection relative to the pre-execution state. Computed from observed scene state, not from static analysis of the code.")]
        public bool Mutated { get; set; }

        [Description("Paths (or 'Untitled') of scenes left dirty by the execution; empty when Mutated is false.")]
        public string[] MutatedScenes { get; set; } = Array.Empty<string>();

        [Description("True when the execution ran in a disposable sandbox scene; null when sandboxScene was not requested.")]
        public bool? SandboxUsed { get; set; }

        [Description("Whether the user's scene setup, selection, and dirty state were restored after a sandboxed execution; null when not sandboxed.")]
        public bool? SandboxRestored { get; set; }

        [Description("Cause reported when a sandboxed execution could not fully restore the captured scene state.")]
        public string? SandboxRestoreCause { get; set; }
    }
}
