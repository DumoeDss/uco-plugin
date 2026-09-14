/*
 * Design inspired by MCP for Unity (CoplayDev/unity-mcp), Copyright (c) Coplay Inc., MIT License.
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
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace com.AtelierAI.Unity.Copilot.Editor.Utils
{
    /// <summary>
    /// Serializable entry describing a single Unity Editor instance discovered
    /// via the on-disk registry. One per Editor process; written by the owning
    /// Editor and consumed read-only by peers.
    /// </summary>
    [Description("Describes a single Unity Editor instance discovered via the on-disk registry.")]
    public class UnityInstanceEntry
    {
        [Description("MCP server port. SHA256 hash of project path -> 20000-29999 range.")]
        public int Port { get; set; }

        [Description("Absolute project root path.")]
        public string ProjectPath { get; set; } = string.Empty;

        [Description("Last path component as friendly name.")]
        public string ProjectName { get; set; } = string.Empty;

        [Description("Unity Editor version.")]
        public string UnityVersion { get; set; } = string.Empty;

        [Description("Process ID of the Editor.")]
        public int ProcessId { get; set; }

        [Description("ISO 8601 UTC timestamp when the entry was last refreshed.")]
        public string LastUpdatedUtc { get; set; } = string.Empty;

        [Description("True if this entry is the calling Editor itself.")]
        public bool IsSelf { get; set; }

        [Description("True if Editor process is still running (verified by checking PID).")]
        public bool IsAlive { get; set; }

        [Description("Stable identifier: {ProjectName}@{first 8 chars of project path SHA256}.")]
        public string InstanceId { get; set; } = string.Empty;
    }

    /// <summary>
    /// On-disk registry that lets concurrent Unity Editors discover each other.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each Editor process owns exactly one JSON file under
    /// <c>%TEMP%/unity-mcp-instances/{port}.json</c> describing itself. The
    /// file is written on domain reload via the <see cref="InitializeOnLoadAttribute"/>
    /// static ctor, refreshed periodically (every 30 s) to keep <c>mtime</c>
    /// fresh, and deleted when the Editor exits cleanly through
    /// <see cref="EditorApplication.quitting"/>.
    /// </para>
    /// <para>
    /// Stale entries (mtime older than 5 minutes) are filtered out by
    /// <see cref="List"/> so a crashed Editor's leftover file is hidden until
    /// it is overwritten or removed. Callers can opt in to see stale rows.
    /// </para>
    /// <para>
    /// <b>Scope.</b> This is a "soft" discovery layer — it only reports who
    /// is alive. Actual cross-instance tool routing requires server-side
    /// support (in the external NuGet <c>com.IvanMurzak.McpPlugin</c>) and
    /// is NOT implemented here.
    /// </para>
    /// </remarks>
    [InitializeOnLoad]
    public static class UnityInstanceRegistry
    {
        public static readonly string RegistryDir = Path.Combine(Path.GetTempPath(), "unity-mcp-instances");
        public static readonly TimeSpan StaleThreshold = TimeSpan.FromMinutes(5);
        static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);

        static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = false,
        };

        static string? _selfFilePath;
        static string? _selfInstanceId;
        static int _selfPort;
        static DateTime _lastRefreshUtc = DateTime.MinValue;

        static UnityInstanceRegistry()
        {
            try
            {
                if (!Directory.Exists(RegistryDir))
                    Directory.CreateDirectory(RegistryDir);

                WriteSelf();
                _lastRefreshUtc = DateTime.UtcNow;

                EditorApplication.quitting += OnQuitting;
                EditorApplication.update += MaybeRefresh;
            }
            catch (Exception ex)
            {
                // Discovery must never block Editor startup. Log and move on.
                Debug.LogWarning($"UnityInstanceRegistry init failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Returns every registry entry currently on disk, with the calling
        /// Editor's own row marked <see cref="UnityInstanceEntry.IsSelf"/>.
        /// </summary>
        /// <param name="includeStale">
        /// When <c>false</c> (default), entries whose <c>mtime</c> is older
        /// than <see cref="StaleThreshold"/> are dropped.
        /// </param>
        public static IReadOnlyList<UnityInstanceEntry> List(bool includeStale = false)
        {
            var result = new List<UnityInstanceEntry>();
            try
            {
                if (!Directory.Exists(RegistryDir))
                    return result;

                var now = DateTime.UtcNow;
                var selfId = _selfInstanceId ?? BuildSelfInstanceId();

                foreach (var path in Directory.EnumerateFiles(RegistryDir, "*.json"))
                {
                    UnityInstanceEntry? entry;
                    try
                    {
                        var json = File.ReadAllText(path);
                        entry = JsonSerializer.Deserialize<UnityInstanceEntry>(json, JsonOptions);
                    }
                    catch
                    {
                        // Corrupt / partially-written file — skip silently.
                        continue;
                    }
                    if (entry == null)
                        continue;

                    var mtime = SafeGetLastWriteUtc(path);
                    var age = now - mtime;
                    var isStale = age > StaleThreshold;

                    if (isStale && !includeStale)
                        continue;

                    entry.IsAlive = !isStale && IsProcessAlive(entry.ProcessId);
                    entry.IsSelf = !string.IsNullOrEmpty(selfId)
                                   && string.Equals(entry.InstanceId, selfId, StringComparison.Ordinal);

                    result.Add(entry);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"UnityInstanceRegistry.List failed: {ex.Message}");
            }

            result.Sort((a, b) => string.CompareOrdinal(a.InstanceId, b.InstanceId));
            return result;
        }

        /// <summary>
        /// Build a fresh entry describing the calling Editor — does not touch disk.
        /// </summary>
        public static UnityInstanceEntry BuildSelfEntry()
        {
            var projectPath = SafeProjectPath();
            var projectName = SafeProjectName(projectPath);
            var instanceId = BuildInstanceId(projectName, projectPath);
            return new UnityInstanceEntry
            {
                Port = SafeReadPort(),
                ProjectPath = projectPath,
                ProjectName = projectName,
                UnityVersion = Application.unityVersion ?? string.Empty,
                ProcessId = SafeProcessId(),
                LastUpdatedUtc = DateTime.UtcNow.ToString("o"),
                InstanceId = instanceId,
                IsSelf = true,
                IsAlive = true,
            };
        }

        // ---------------------------------------------------------------------
        // Private helpers
        // ---------------------------------------------------------------------

        static void MaybeRefresh()
        {
            try
            {
                if ((DateTime.UtcNow - _lastRefreshUtc) < RefreshInterval)
                    return;

                WriteSelf();
                _lastRefreshUtc = DateTime.UtcNow;
            }
            catch
            {
                // Periodic refresh must never escalate; swallow.
            }
        }

        static void WriteSelf()
        {
            var entry = BuildSelfEntry();
            _selfInstanceId = entry.InstanceId;
            _selfPort = entry.Port;

            // Use port as filename so each Editor owns exactly one file. If two
            // Editors ever collide on port (project-path hash collision), the
            // later writer wins — the prior entry will be overwritten and the
            // dead Editor's PID will then resolve to !IsAlive.
            var filePath = Path.Combine(RegistryDir, $"{entry.Port}.json");
            _selfFilePath = filePath;

            var json = JsonSerializer.Serialize(entry, JsonOptions);
            // Atomic-ish write: write next to it, then move over. Falls back
            // to direct write if the temp file approach fails (e.g. tmp dir
            // permissions on locked-down CI runners).
            try
            {
                var tmpPath = filePath + ".tmp";
                File.WriteAllText(tmpPath, json, Encoding.UTF8);
                if (File.Exists(filePath))
                    File.Delete(filePath);
                File.Move(tmpPath, filePath);
            }
            catch
            {
                File.WriteAllText(filePath, json, Encoding.UTF8);
            }
        }

        static void OnQuitting()
        {
            try
            {
                if (!string.IsNullOrEmpty(_selfFilePath) && File.Exists(_selfFilePath))
                    File.Delete(_selfFilePath);
            }
            catch
            {
                // Cleanup is best-effort; stale row will time out via StaleThreshold.
            }
        }

        static int SafeReadPort()
        {
            try { return UnityCopilotPluginEditor.Port; }
            catch { return 0; }
        }

        static int SafeProcessId()
        {
            try { return Process.GetCurrentProcess().Id; }
            catch { return 0; }
        }

        static string SafeProjectPath()
        {
            try { return Path.GetDirectoryName(Application.dataPath) ?? string.Empty; }
            catch { return string.Empty; }
        }

        static string SafeProjectName(string projectPath)
        {
            if (string.IsNullOrEmpty(projectPath))
                return string.Empty;
            try { return Path.GetFileName(projectPath) ?? string.Empty; }
            catch { return string.Empty; }
        }

        static string BuildSelfInstanceId()
        {
            var projectPath = SafeProjectPath();
            var projectName = SafeProjectName(projectPath);
            var id = BuildInstanceId(projectName, projectPath);
            _selfInstanceId = id;
            return id;
        }

        static string BuildInstanceId(string projectName, string projectPath)
        {
            if (string.IsNullOrEmpty(projectPath))
                return string.IsNullOrEmpty(projectName) ? "unknown@00000000" : $"{projectName}@00000000";

            byte[] sha;
            using (var hasher = SHA256.Create())
                sha = hasher.ComputeHash(Encoding.UTF8.GetBytes(projectPath));
            // 8 hex chars = first 4 bytes; matches the spec ("first 8 chars of project path SHA256").
            var suffix = BitConverter.ToString(sha, 0, 4).Replace("-", string.Empty).ToLowerInvariant();
            var name = string.IsNullOrEmpty(projectName) ? "unknown" : projectName;
            return $"{name}@{suffix}";
        }

        static bool IsProcessAlive(int pid)
        {
            if (pid <= 0)
                return false;
            try
            {
                using var p = Process.GetProcessById(pid);
                return p != null && !p.HasExited;
            }
            catch
            {
                // ArgumentException when the PID is not running; InvalidOperationException
                // for already-exited processes; both = not alive.
                return false;
            }
        }

        static DateTime SafeGetLastWriteUtc(string path)
        {
            try { return File.GetLastWriteTimeUtc(path); }
            catch { return DateTime.MinValue; }
        }
    }
}
