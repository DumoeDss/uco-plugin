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
using System.Collections.Generic;
using System.ComponentModel;

namespace com.AtelierAI.Unity.Copilot.Editor.API.TestRunner
{
    public class TestRunResponse
    {
        [Description("Summary of the test run including total, passed, failed, and skipped counts.")]
        public TestSummaryData Summary { get; set; } = new TestSummaryData();

        [Description("List of individual test results with details about each test.")]
        public List<TestResultData> Results { get; set; } = new List<TestResultData>();

        [Description("Log entries captured during test execution.")]
        public List<TestLogEntry>? Logs { get; set; }

        [Description("True when the run left any open scene dirty or changed the active scene or selection relative to the pre-run state. Computed from observed scene state after the run.")]
        public bool Mutated { get; set; }

        [Description("Paths (or 'Untitled') of scenes left dirty by the run; empty when Mutated is false.")]
        public string[] MutatedScenes { get; set; } = Array.Empty<string>();

        [Description("Whether the sandbox scene state was restored after a sandboxed run; null when the run was not sandboxed.")]
        public bool? SandboxRestored { get; set; }

        [Description("Cause reported when a sandboxed run could not fully restore the captured scene state.")]
        public string? SandboxRestoreCause { get; set; }
    }
}
