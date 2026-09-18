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
using com.AtelierAI.Unity.Copilot.Editor.Branding;
using UnityEditor;
using UnityEngine;

namespace com.AtelierAI.Unity.Copilot.Editor.Utils
{
    // Phase C rebrand carries forward existing user state across the
    // "com.ivanmurzak.unity.plugin" → "com.atelierai.unity.copilot" key namespace shift.
    // Runs once per project (guarded by MigrationDoneKey) on domain reload and copies
    // old EditorPrefs keys into the new namespace. Old keys are left in place — they're
    // harmless and lets users downgrade if the rebrand is reverted. A future major
    // version can delete this class and the old keys together.
    [InitializeOnLoad]
    internal static class PrefsKeyMigration
    {
        // Bumped when migration logic changes. Stored under the NEW prefix so the
        // flag survives the rename and only triggers once per (project, version).
        private const int MigrationVersion = 1;
        private static readonly string MigrationDoneKey =
            $"{ProductInfo.PackageId}.{Fnv1a32(Application.dataPath):X8}.PrefsKeyMigration.Version";

        static PrefsKeyMigration()
        {
            try
            {
                if (EditorPrefs.GetInt(MigrationDoneKey, 0) >= MigrationVersion)
                    return;

                MigrateToolGroupsEnabled();
                MigrateUpdateCheckerKeys();
                MigrateServerProcessId();

                EditorPrefs.SetInt(MigrationDoneKey, MigrationVersion);
            }
            catch (System.Exception ex)
            {
                // Migration failure must never break Editor startup. Worst case the user
                // re-sees the update popup once or re-configures tool groups.
                Debug.LogWarning($"{ProductInfo.LogPrefix} PrefsKeyMigration skipped: {ex.Message}");
            }
        }

        // ToolGroupRegistry persists enabled tool-group names under a flat key — no
        // per-project hash because the registry is project-local already (Unity creates
        // a fresh EditorPrefs scope per install, and the value is just a CSV).
        private static void MigrateToolGroupsEnabled()
        {
            const string oldKey = "UnityMcp.ToolGroups.Enabled";
            string newKey = $"{ProductInfo.EditorPrefsPrefix}.ToolGroups.Enabled";
            if (oldKey == newKey) return;
            if (!EditorPrefs.HasKey(oldKey)) return;
            if (EditorPrefs.HasKey(newKey)) return;   // user already configured under new name
            EditorPrefs.SetString(newKey, EditorPrefs.GetString(oldKey));
        }

        // UpdateChecker namespaces keys by (packageId, FNV-1a(Application.dataPath)) so a
        // user with multiple Unity projects keeps independent "Do not show again" state.
        // After rebrand the packageId segment changes, so for THIS project's hash we copy
        // each of the three sub-keys forward.
        private static void MigrateUpdateCheckerKeys()
        {
            string hash = $"{Fnv1a32(Application.dataPath):X8}";
            string oldPrefix = $"{ProductInfo.LegacyPackageId}.{hash}.UpdateChecker.";
            string newPrefix = $"{ProductInfo.PackageId}.{hash}.UpdateChecker.";
            if (oldPrefix == newPrefix) return;

            CopyStringIfMissing(oldPrefix + "DoNotShowAgain", newPrefix + "DoNotShowAgain");
            CopyStringIfMissing(oldPrefix + "NextCheckTime",  newPrefix + "NextCheckTime");
            CopyStringIfMissing(oldPrefix + "SkippedVersion", newPrefix + "SkippedVersion");
        }

        // ServerManager pid is transient (server respawns) but copying avoids a stale
        // "is the server already running?" probe on the first reload after rebrand.
        private static void MigrateServerProcessId()
        {
            const string oldKey = "McpServerManager_ProcessId";
            const string newKey = "CopilotServerManager_ProcessId";
            if (!EditorPrefs.HasKey(oldKey)) return;
            if (EditorPrefs.HasKey(newKey)) return;
            EditorPrefs.SetInt(newKey, EditorPrefs.GetInt(oldKey));
        }

        private static void CopyStringIfMissing(string oldKey, string newKey)
        {
            if (!EditorPrefs.HasKey(oldKey)) return;
            if (EditorPrefs.HasKey(newKey)) return;
            EditorPrefs.SetString(newKey, EditorPrefs.GetString(oldKey));
        }

        // Duplicated from UpdateChecker.cs — extracting to a shared util is a larger
        // refactor than this phase warrants. Same FNV-1a (offsetBasis 2166136261,
        // prime 16777619), same per-UTF-16-codepoint two-byte loop.
        private static uint Fnv1a32(string input)
        {
            const uint offsetBasis = 2166136261u;
            const uint prime = 16777619u;
            var hash = offsetBasis;
            for (int i = 0; i < input.Length; i++)
            {
                hash ^= (byte)(input[i] & 0xFF);
                hash *= prime;
                hash ^= (byte)((input[i] >> 8) & 0xFF);
                hash *= prime;
            }
            return hash;
        }
    }
}
