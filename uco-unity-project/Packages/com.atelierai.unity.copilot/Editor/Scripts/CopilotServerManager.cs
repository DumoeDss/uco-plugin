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
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using com.IvanMurzak.ReflectorNet.Utils;
using com.AtelierAI.Unity.Copilot.Editor.Utils;
using com.AtelierAI.Unity.Copilot.Runtime.Utils;
using com.AtelierAI.Unity.Copilot.Utils;
using Microsoft.Extensions.Logging;
using R3;
using UnityEditor;

namespace com.AtelierAI.Unity.Copilot.Editor
{
    using static com.AtelierAI.Uco.Framework.Common.Consts.MCP.Server;
    using Consts = com.AtelierAI.Uco.Framework.Common.Consts;
    using ILogger = Microsoft.Extensions.Logging.ILogger;

    public enum CopilotServerStatus
    {
        Stopped,
        Starting,
        Running,
        Stopping,
        External
    }

    /// <summary>
    /// Manages the Node.js MCP server (cocli) process lifecycle independently from UI.
    /// The server entry script is discovered from the plugin config (nodeServerPath),
    /// the Unity project's node_modules, or the npm global installation — the plugin no
    /// longer stages or downloads a server binary into Library/.
    /// Provides cross-platform support for Windows, macOS, and Linux.
    /// </summary>
    [InitializeOnLoad]
    public static class CopilotServerManager
    {
        const string ProcessIdKey = "CopilotServerManager_ProcessId";

        // The Node.js MCP server runs as a "node" process.
        const string NodeProcessName = "node";

        static readonly ILogger _logger = UnityLoggerFactory.LoggerFactory.CreateLogger(typeof(CopilotServerManager));
        static readonly ReactiveProperty<CopilotServerStatus> _serverStatus = new(CopilotServerStatus.Stopped);
        static readonly object _processMutex = new();

        static Process? _serverProcess;

        public static ReadOnlyReactiveProperty<CopilotServerStatus> ServerStatus => _serverStatus;

        public static bool IsRunning => _serverStatus.CurrentValue == CopilotServerStatus.Running;
        public static bool IsStarting => _serverStatus.CurrentValue == CopilotServerStatus.Starting;

        static CopilotServerManager()
        {
            if (!UnityProcessGuard.ShouldInitialize(AssetDatabase.IsAssetImportWorkerProcess()))
                return;

            // Register for editor quit to clean up the server process
            EditorApplication.quitting += OnEditorQuitting;

            // Check if server process is still running (e.g., after domain reload)
            EditorApplication.update += CheckExistingProcess;

            if (EnvironmentUtils.IsCi())
                return; // Skip auto-start in CI environment

            EditorApplication.update += StartServerIfNeeded;
        }

        #region Node Server Discovery

        /// <summary>
        /// Name of the npm package that ships the Node.js server.
        /// </summary>
        public const string NodeServerPackageName = "uco";

        /// <summary>
        /// Path of the server entry script inside the <see cref="NodeServerPackageName"/> package.
        /// </summary>
        public const string NodeServerEntryRelativePath = "bin/server.mjs";

        /// <summary>
        /// Resolves the Node.js server entry script (uco's bin/server.mjs) to launch.
        /// Lookup order:
        ///  1. Explicit <c>nodeServerPath</c> from the plugin config (absolute, or relative
        ///     to the Unity project root) — when set, it must exist or launching is refused.
        ///  2. uco installed in the Unity project's node_modules.
        ///  3. uco installed globally via npm.
        /// Returns null when nothing is found — the caller must not launch in that case,
        /// but the plugin can still connect to an already-running server.
        /// </summary>
        public static string? ResolveNodeServerEntry()
        {
            // 1) Explicit path from the config
            var configured = UnityCopilotPluginEditor.NodeServerPath;
            if (!string.IsNullOrWhiteSpace(configured))
            {
                var path = Path.GetFullPath(Path.IsPathRooted(configured)
                    ? configured
                    : Path.Combine(UnityCopilotPluginEditor.ProjectRootPath, configured));

                if (File.Exists(path))
                    return path;

                _logger.LogError(
                    "nodeServerPath is set in the plugin config but does not exist: {path}. " +
                    "Fix the path or clear it to let the plugin auto-discover the server.",
                    path);
                return null;
            }

            // 2) Installed in the Unity project (uco first, legacy cocli as fallback)
            // 2) uco installed in the Unity project
            var projectLocal = Path.GetFullPath(Path.Combine(
                UnityCopilotPluginEditor.ProjectRootPath,
                "node_modules",
                NodeServerPackageName,
                NodeServerEntryRelativePath));
            if (File.Exists(projectLocal))
                return projectLocal;

            // 3) uco installed globally via npm
            foreach (var globalRoot in GetNpmGlobalRoots())
            {
                try
                {
                    var globalPath = Path.Combine(globalRoot, NodeServerPackageName, NodeServerEntryRelativePath);
                    if (File.Exists(globalPath))
                        return Path.GetFullPath(globalPath);
                }
                catch
                {
                    // Inaccessible candidate — skip it.
                }
            }

            return null;
        }

        /// <summary>
        /// Standard npm global installation roots (the <c>npm root -g</c> equivalents).
        /// </summary>
        static IEnumerable<string> GetNpmGlobalRoots()
        {
            // Windows: %AppData%\npm\node_modules
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (!string.IsNullOrEmpty(appData))
                yield return Path.Combine(appData, "npm", "node_modules");

            // Unix: default prefixes for system-wide npm installs
            yield return "/usr/local/lib/node_modules";
            yield return "/usr/lib/node_modules";
        }

        /// <summary>
        /// Resolves the Node.js runtime executable. Probes PATH explicitly because
        /// Process.Start with UseShellExecute=false does not reliably search PATH for
        /// the application name on every runtime Unity ships with. Falls back to the
        /// bare name so runtimes that do search PATH still work.
        /// </summary>
        public static string ResolveNodeExecutable()
        {
            var fileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "node.exe" : "node";

            var candidates = new List<string>();
            var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            var separator = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ';' : ':';
            foreach (var directory in pathVariable.Split(separator, StringSplitOptions.RemoveEmptyEntries))
            {
                try { candidates.Add(Path.Combine(directory.Trim().Trim('"'), fileName)); }
                catch { /* Malformed PATH entry — skip it. */ }
            }

            // Common install locations, in case node is not on the editor's PATH
            // (e.g. Unity launched from a GUI session with a stale environment).
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrEmpty(programFiles))
                candidates.Add(Path.Combine(programFiles, "nodejs", fileName));
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (!string.IsNullOrEmpty(programFilesX86))
                candidates.Add(Path.Combine(programFilesX86, "nodejs", fileName));

            foreach (var candidate in candidates)
            {
                try
                {
                    if (File.Exists(candidate))
                        return candidate;
                }
                catch
                {
                    // Inaccessible candidate — skip it.
                }
            }

            // Fall back to the bare name and let the OS resolve it.
            return fileName;
        }

        #endregion // Node Server Discovery

        #region Client Configuration

        /// <summary>
        /// Generates a JSON configuration for stdio transport.
        /// <code>
        /// {
        ///   "mcpServers": {
        ///     "Unity ProjectName": {
        ///       "type": "...",    // optional, only if provided
        ///       "command": "node",
        ///       "args": ["path/to/bin/server.mjs", "--port", "...", "--plugin-timeout-ms", "...", "--authorization", "..." /*, "--token", "..." if auth required */]
        ///     }
        ///   }
        /// }
        /// </code>
        /// </summary>
        public static JsonNode RawJsonConfigurationStdio(
            int port,
            string bodyPath = "mcpServers",
            int timeoutMs = Consts.Hub.DefaultTimeoutMs,
            string? type = null)
        {
            var pathSegments = BodyPathSegments(bodyPath);

            // Build innermost content first
            var serverConfig = new JsonObject();

            if (type != null)
                serverConfig["type"] = type;

            // The AI agent launches the Node.js MCP server via the node runtime.
            // When the entry script cannot be resolved right now, fall back to the
            // documented project-local install location so the generated config is
            // still a valid, copy-pasteable starting point.
            var serverEntry = ResolveNodeServerEntry()
                ?? Path.Combine("node_modules", NodeServerPackageName, NodeServerEntryRelativePath);

            serverConfig["command"] = ResolveNodeExecutable().Replace('\\', '/');

            var args = new JsonArray
            {
                serverEntry.Replace('\\', '/'),
                "--port",
                port.ToString(),
                "--plugin-timeout-ms",
                timeoutMs.ToString(),
                "--authorization",
                UnityCopilotPluginEditor.AuthOption.ToString()
            };

            var authRequired = UnityCopilotPluginEditor.AuthOption == AuthOption.required;
            if (authRequired && !string.IsNullOrEmpty(UnityCopilotPluginEditor.Token))
            {
                args.Add("--token");
                args.Add(UnityCopilotPluginEditor.Token);
            }

            serverConfig["args"] = args;

            var innerContent = new JsonObject
            {
                ["ai-game-developer"] = serverConfig
            };

            // Build nested structure from innermost to outermost
            var result = innerContent;
            for (int i = pathSegments.Length - 1; i >= 0; i--)
            {
                result = new JsonObject { [pathSegments[i]] = result };
            }

            return result;
        }

        /// <summary>
        /// Generates a JSON configuration for HTTP transport.
        /// <code>
        /// {
        ///   "mcpServers": {
        ///     "Unity ProjectName": {
        ///       "type": "...",  // optional, only if provided
        ///       "url": "http://localhost:port",
        ///      "headers": {     // only if token is provided
        ///        "Authorization": "Bearer token"
        ///      }
        ///     }
        ///   }
        /// }
        /// </code>
        /// </summary>
        public static JsonNode RawJsonConfigurationHttp(
            string url,
            string bodyPath = "mcpServers",
            string? type = null)
        {
            // ── Safe-defaults advisory ────────────────────────────────────────────────
            // If the URL targets a remote host over plain HTTP and the user has not opted in
            // to insecure remote HTTP, log a single warning. We deliberately do NOT throw or
            // rewrite the URL here — the configurators are expected to produce *some* output
            // even when the configuration is sub-optimal, so the user can see what was
            // generated and decide. The dialog/UI gate handles refusal at start time.
            if (SafeDefaultsGuard.IsInsecureRemoteHttpUrl(url) &&
                !UnityCopilotPluginEditor.AllowInsecureRemoteHttp)
            {
                _logger.LogWarning(
                    "Generating AI agent client config for plain-HTTP remote URL '{url}' without " +
                    "AllowInsecureRemoteHttp. The token (if any) and tool payloads will be " +
                    "sent unencrypted. Either switch to https:// or enable 'Allow Insecure " +
                    "Remote HTTP' in the Game Developer window to silence this warning.",
                    url);
            }

            var pathSegments = BodyPathSegments(bodyPath);

            // Build innermost content first
            var serverConfig = new JsonObject();

            if (type != null)
                serverConfig["type"] = type;

            serverConfig["url"] = url;

            var authRequired = UnityCopilotPluginEditor.AuthOption == AuthOption.required;
            if (authRequired && !string.IsNullOrEmpty(UnityCopilotPluginEditor.Token))
            {
                serverConfig["headers"] = new JsonObject
                {
                    ["Authorization"] = $"Bearer {UnityCopilotPluginEditor.Token}"
                };
            }

            var innerContent = new JsonObject
            {
                ["ai-game-developer"] = serverConfig
            };

            // Build nested structure from innermost to outermost
            var result = innerContent;
            for (int i = pathSegments.Length - 1; i >= 0; i--)
            {
                result = new JsonObject { [pathSegments[i]] = result };
            }

            return result;
        }

        #endregion // Client Configuration

        #region Process Lifecycle

        static void CheckExistingProcess()
        {
            EditorApplication.update -= CheckExistingProcess;
            // Try to find an existing server process by checking if our tracked PID is still running
            // This helps maintain state across domain reloads
            var savedPid = EditorPrefs.GetInt(ProcessIdKey, -1);
            if (savedPid > 0)
            {
                try
                {
                    var process = Process.GetProcessById(savedPid);
                    if (process != null && !process.HasExited)
                    {
                        var processName = process.ProcessName.ToLowerInvariant();
                        // The server runs as a "node" process, and PIDs get reused, so also
                        // verify the process still owns this project's port before
                        // re-attaching — otherwise we might adopt an unrelated node process
                        // (MCP Inspector, dev server, ...) that recycled our PID.
                        if (processName.Contains(NodeProcessName) &&
                            GetPidListeningOnPort(UnityCopilotPluginEditor.Port) == savedPid)
                        {
                            _serverProcess = process;
                            _serverStatus.Value = CopilotServerStatus.Running;
                            _logger.LogInformation("Reconnected to existing server process (PID: {pid})", savedPid);

                            // Re-attach exit handler
                            process.EnableRaisingEvents = true;
                            process.Exited += OnProcessExited;

                            // Schedule verification check to detect if process crashes shortly after reconnection
                            ScheduleStartupVerification(savedPid);
                            return;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug("Could not reconnect to previous process: {message}", ex.Message);
                }

                // Clear stale PID
                EditorPrefs.DeleteKey(ProcessIdKey);
            }
        }

        static void OnEditorQuitting()
        {
            StopServer(force: true);
        }

        public static bool StartServer()
        {
            lock (_processMutex)
            {
                if (_serverStatus.CurrentValue == CopilotServerStatus.Running ||
                    _serverStatus.CurrentValue == CopilotServerStatus.Starting ||
                    _serverStatus.CurrentValue == CopilotServerStatus.Stopping)
                {
                    _logger.LogWarning("server is already {status}", _serverStatus.CurrentValue);
                    return false;
                }

                // ── Safe-defaults (fail-closed) gate ──────────────────────────────────────
                // Refuse to start the local server when the current configuration would
                // expose it to the network without explicit opt-in. The gate is consulted
                // BEFORE we mutate _serverStatus / spawn the process so a reject leaves the
                // manager in a fully clean state. See SafeDefaultsGuard for the full policy.
                if (SafeDefaultsGuard.ShouldRejectStart(out var rejectReason))
                {
                    _logger.LogError("Refusing to start server: {reason}", rejectReason);
                    var openSettings = EditorUtility.DisplayDialog(
                        title: "Server: unsafe configuration",
                        message: rejectReason,
                        ok: "Open Settings",
                        cancel: "Cancel");
                    if (openSettings)
                    {
                        try { com.AtelierAI.Unity.Copilot.Editor.UI.MainWindowEditor.ShowWindowVoid(); }
                        catch (Exception openEx)
                        {
                            _logger.LogDebug("Failed to open Game Developer window: {message}", openEx.Message);
                        }
                    }
                    return false;
                }

                // If something already listens on this project's port, treat it as an
                // already-running server (e.g. started externally by the user or a
                // previous editor session). Do not launch a second instance — just let
                // the plugin's connect flow attach to it.
                var ownPid = -1;
                try { ownPid = _serverProcess?.Id ?? -1; } catch { /* process gone */ }
                var listeningPid = GetPidListeningOnPort(UnityCopilotPluginEditor.Port);
                if (listeningPid > 0 && listeningPid != ownPid)
                {
                    _logger.LogInformation(
                        "A server is already listening on port {port} (PID: {pid}) — skipping local launch, the plugin will connect to it",
                        UnityCopilotPluginEditor.Port, listeningPid);
                    return true;
                }

                var serverEntry = ResolveNodeServerEntry();
                if (serverEntry == null)
                {
                    _logger.LogError(
                        "Node.js MCP server entry not found. Install cocli (npm i -g cocli), or install it into the " +
                        "Unity project (npm i cocli), or set 'nodeServerPath' in '{config}' to the absolute path of " +
                        "cocli's bin/server.mjs. The plugin will still connect to an already-running server.",
                        UnityCopilotPluginEditor.AssetsFilePath);
                    return false;
                }

                _serverStatus.Value = CopilotServerStatus.Starting;

                try
                {
                    var nodeExecutable = ResolveNodeExecutable();
                    var argumentList = BuildArgumentList(serverEntry);

                    _logger.LogInformation("Starting server: {path} {args}", nodeExecutable, string.Join(" ", argumentList));

                    var startInfo = new ProcessStartInfo
                    {
                        FileName = nodeExecutable,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        WorkingDirectory = UnityCopilotPluginEditor.ProjectRootPath
                    };

                    // Use ArgumentList (Collection<string>) rather than the legacy single
                    // Arguments string so that any argument containing spaces or quote
                    // characters is escaped correctly by the runtime. Required because the
                    // server entry path may live under a project path that contains spaces.
                    foreach (var arg in argumentList)
                        startInfo.ArgumentList.Add(arg);

                    _serverProcess = new Process
                    {
                        StartInfo = startInfo,
                        EnableRaisingEvents = true
                    };
                    _serverProcess.Exited += OnProcessExited;
                    _serverProcess.OutputDataReceived += OnOutputDataReceived;
                    _serverProcess.ErrorDataReceived += OnErrorDataReceived;

                    if (!_serverProcess.Start())
                    {
                        _logger.LogError("Failed to start server process");
                        CleanupProcess();
                        return false;
                    }

                    _serverProcess.BeginOutputReadLine();
                    _serverProcess.BeginErrorReadLine();

                    // Save PID for reconnection after domain reload
                    EditorPrefs.SetInt(ProcessIdKey, _serverProcess.Id);

                    // Keep status as Starting - it will be set to Running after verification
                    _logger.LogInformation("server process started (PID: {pid}), awaiting verification...", _serverProcess.Id);

                    // Schedule a delayed check to verify the process is still running
                    // This catches early crashes that might not trigger the Exited event reliably
                    // Status will be set to Running only after successful verification
                    ScheduleStartupVerification(_serverProcess.Id);

                    return true;
                }
                catch (Exception ex)
                {
                    _logger.LogError("Failed to start server: {message}", ex.Message);
                    CleanupProcess();
                    return false;
                }
            }
        }

        /// <summary>
        /// Stops the server process.
        /// By default, this method is non-blocking: it sends the kill/terminate signal
        /// and lets the Exited event handler perform cleanup asynchronously.
        /// When force is true (e.g., editor quitting), it blocks until the process exits.
        /// </summary>
        public static bool StopServer(bool force = false)
        {
            lock (_processMutex)
            {
                if (_serverStatus.CurrentValue == CopilotServerStatus.Stopped ||
                    _serverStatus.CurrentValue == CopilotServerStatus.Stopping)
                {
                    _logger.LogDebug("server is already stopped or stopping");
                    return true;
                }

                if (_serverProcess == null)
                {
                    _serverStatus.Value = CopilotServerStatus.Stopped;
                    EditorPrefs.DeleteKey(ProcessIdKey);
                    return true;
                }

                _serverStatus.Value = CopilotServerStatus.Stopping;

                try
                {
                    _logger.LogInformation("Stopping server (PID: {pid})", _serverProcess.Id);

                    if (!_serverProcess.HasExited)
                    {
                        SendTerminateSignal();
                    }

                    if (force)
                    {
                        // Synchronous path: block until exit (used during editor quitting)
                        WaitForExitAndForceKillIfNeeded();
                        CleanupProcess();
                    }
                    else
                    {
                        if (_serverProcess.HasExited)
                        {
                            CleanupProcess();
                        }
                        else
                        {
                            // Non-blocking path: schedule background wait + force kill safety net.
                            // CleanupProcess will be called by OnProcessExited or the background task.
                            ScheduleForceKillIfNeeded();
                        }
                    }

                    _logger.LogInformation("server stop initiated");
                    return true;
                }
                catch (Exception ex)
                {
                    _logger.LogError("Error stopping server: {message}", ex.Message);
                    CleanupProcess();
                    return false;
                }
            }
        }

        /// <summary>
        /// Sends the platform-appropriate terminate signal without waiting for exit.
        /// </summary>
        static void SendTerminateSignal()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                _serverProcess!.Kill();
            }
            else
            {
                // On Unix-like systems, send SIGTERM for graceful shutdown
                try
                {
                    using var killProcess = Process.Start(new ProcessStartInfo
                    {
                        FileName = "kill",
                        Arguments = $"-TERM {_serverProcess!.Id}",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                    killProcess?.WaitForExit(1000);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug("SIGTERM failed, falling back to Kill(): {message}", ex.Message);
                    _serverProcess!.Kill();
                }
            }
        }

        /// <summary>
        /// Blocking wait for process exit, with force-kill fallback.
        /// Used only during editor quitting to prevent orphaned processes.
        /// </summary>
        static void WaitForExitAndForceKillIfNeeded()
        {
            if (_serverProcess == null || _serverProcess.HasExited)
                return;

            if (!_serverProcess.WaitForExit(5000))
            {
                _logger.LogWarning("server did not exit gracefully, forcing termination");
                try
                {
                    _serverProcess.Kill();
                    _serverProcess.WaitForExit(2000);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug("Force kill failed: {message}", ex.Message);
                }
            }
        }

        /// <summary>
        /// Background safety net: waits for the process to exit and force-kills after timeout.
        /// Calls CleanupProcess on the main thread when done.
        /// </summary>
        static void ScheduleForceKillIfNeeded()
        {
            var process = _serverProcess;
            if (process == null)
                return;

            Task.Run(() =>
            {
                try
                {
                    if (!process.HasExited && !process.WaitForExit(5000))
                    {
                        _logger.LogWarning("server did not exit gracefully, forcing termination");
                        try
                        {
                            process.Kill();
                            process.WaitForExit(2000);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug("Force kill error: {message}", ex.Message);
                        }
                    }
                }
                catch (InvalidOperationException ex)
                {
                    _logger.LogDebug("Process already exited or disposed while waiting for exit: {message}", ex.Message);
                }

                // Ensure cleanup on the main thread.
                // Safe to call even if OnProcessExited already triggered cleanup.
                MainThread.Instance.Run(CleanupProcess);
            });
        }

        /// <summary>
        /// Returns the PID of the process listening on the specified TCP port,
        /// or -1 if no process is found or the lookup fails.
        /// </summary>
        static int GetPidListeningOnPort(int port)
        {
            try
            {
                var startInfo = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                    ? new ProcessStartInfo
                    {
                        FileName = "netstat",
                        Arguments = "-ano -p tcp",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true
                    }
                    : new ProcessStartInfo
                    {
                        FileName = "lsof",
                        Arguments = $"-ti tcp:{port} -sTCP:LISTEN",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };

                using var process = Process.Start(startInfo);
                if (process == null) return -1;

                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(5000);

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    var portSuffix = $":{port}";
                    foreach (var line in output.Split('\n'))
                    {
                        var trimmed = line.Trim();
                        if (!trimmed.Contains("LISTENING"))
                            continue;

                        var parts = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length < 5)
                            continue;

                        var localAddress = parts[1];
                        if (localAddress.EndsWith(portSuffix) && int.TryParse(parts[parts.Length - 1], out var pid))
                            return pid;
                    }
                }
                else
                {
                    var trimmed = output.Trim();
                    if (string.IsNullOrEmpty(trimmed))
                        return -1;

                    var firstLine = trimmed.Split('\n')[0].Trim();
                    if (int.TryParse(firstLine, out var pid))
                        return pid;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Failed to determine PID listening on port {port}: {message}", port, ex.Message);
            }

            return -1;
        }

        /// <summary>
        /// Builds the argument list for the Node.js MCP server as a structured collection
        /// so each entry can be passed to <see cref="ProcessStartInfo.ArgumentList"/>
        /// verbatim. The runtime escapes each entry individually — safe for values that
        /// contain spaces, quotes, or other characters the shell would otherwise interpret.
        /// </summary>
        static IReadOnlyList<string> BuildArgumentList(string serverEntry)
        {
            var port = UnityCopilotPluginEditor.Port;
            var timeout = UnityCopilotPluginEditor.TimeoutMs;
            var token = UnityCopilotPluginEditor.Token;
            var authOption = UnityCopilotPluginEditor.AuthOption;

            // The plugin always talks to the server over REST + WebSocket
            // (streamable HTTP transport), so no client-transport flag is needed.
            var args = new List<string>(9)
            {
                serverEntry,
                "--port", port.ToString(),
                "--plugin-timeout-ms", timeout.ToString(),
                "--authorization", authOption.ToString()
            };

            if (authOption == AuthOption.required && !string.IsNullOrEmpty(token))
            {
                args.Add("--token");
                args.Add(token!);
            }

            return args;
        }

        /// <summary>
        /// Schedules a verification check 5 seconds after startup to detect early crashes.
        /// If the process is still running after verification, the status is set to Running.
        /// If the process has exited and no longer exists, the status is set to Stopped.
        /// </summary>
        static void ScheduleStartupVerification(int processId)
        {
            var startTime = DateTime.UtcNow;
            const double verificationDelaySeconds = 5.0;

            void CheckProcess()
            {
                // If status is no longer Starting (e.g., OnProcessExited already cleaned up), unsubscribe
                if (_serverStatus.CurrentValue != CopilotServerStatus.Starting)
                {
                    EditorApplication.update -= CheckProcess;
                    return;
                }

                var elapsed = DateTime.UtcNow - startTime;

                // If we haven't reached verification delay yet, wait for next frame
                if (elapsed.TotalSeconds < verificationDelaySeconds)
                    return;

                // Detect early process exit before the verification delay
                // This catches crashes that happen within the first few seconds (e.g., port already in use)
                if (!IsProcessRunning(processId))
                {
                    _logger.LogError("server process (PID: {pid}) exited early within {seconds:F1} seconds after launch",
                        processId, elapsed.TotalSeconds);

                    EditorApplication.update -= CheckProcess;
                    if (_serverStatus.CurrentValue == CopilotServerStatus.Starting)
                        CleanupProcess();
                    return;
                }

                // Process is still running after the verification delay - mark as Running
                _logger.LogDebug("server process (PID: {pid}) is still running after {seconds:F1}s verification",
                    processId, elapsed.TotalSeconds);

                EditorApplication.update -= CheckProcess;
                if (_serverStatus.CurrentValue == CopilotServerStatus.Starting)
                {
                    _serverStatus.Value = CopilotServerStatus.Running;
                    _logger.LogInformation("server verified and running (PID: {pid})", processId);
                }
            }

            EditorApplication.update += CheckProcess;
        }

        /// <summary>
        /// Checks if a process with the given ID is still running and is the Node server.
        /// </summary>
        static bool IsProcessRunning(int processId)
        {
            try
            {
                var process = Process.GetProcessById(processId);
                if (process == null || process.HasExited)
                    return false;

                var processName = process.ProcessName.ToLowerInvariant();
                return processName.Contains(NodeProcessName);
            }
            catch (ArgumentException)
            {
                // Process with this ID does not exist
                return false;
            }
            catch (InvalidOperationException)
            {
                // Process has exited
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Error checking process status: {message}", ex.Message);
                return false;
            }
        }

        static void OnProcessExited(object? sender, EventArgs e)
        {
            _logger.LogInformation("server process exited");
            // Marshal to main thread since this event is raised from a thread pool thread
            // and CleanupProcess modifies reactive properties that may be observed on the main thread
            MainThread.Instance.Run(CleanupProcess);
        }

        static void OnOutputDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                _logger.LogDebug("[Server] {output}", e.Data);
            }
        }

        static void OnErrorDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                _logger.LogWarning("[Server Error] {error}", e.Data);
            }
        }

        static void CleanupProcess()
        {
            _logger.LogDebug("Cleaning up server process resources");
            lock (_processMutex)
            {
                var processToDispose = _serverProcess;
                _serverProcess = null;

                if (processToDispose != null)
                {
                    processToDispose.Exited -= OnProcessExited;
                    processToDispose.OutputDataReceived -= OnOutputDataReceived;
                    processToDispose.ErrorDataReceived -= OnErrorDataReceived;

                    // Dispose on a background thread to prevent deadlock.
                    // Process.Dispose() can hang on the main thread when redirected
                    // stdout/stderr streams are active, even after CancelOutputRead/CancelErrorRead.
                    Task.Run(() =>
                    {
                        try
                        {
                            try { processToDispose.CancelOutputRead(); } catch { }
                            try { processToDispose.CancelErrorRead(); } catch { }
                            processToDispose.Dispose();
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug("Error disposing server process: {message}", ex.Message);
                        }
                    });
                }

                EditorPrefs.DeleteKey(ProcessIdKey);
                _serverStatus.Value = CopilotServerStatus.Stopped;
            }
        }

        /// <summary>
        /// Returns true when the local server may be auto-started for the given connection mode.
        /// Only Custom mode targets the local server, so auto-start is allowed there (subject to
        /// other gates such as <see cref="UnityCopilotPluginEditor.KeepServerRunning"/>). Every other
        /// mode (Cloud today, plus any future addition) connects to a remote endpoint and must
        /// never auto-start the local server on Editor launch or after a binary update.
        /// Pure (no Unity API access) so it can be unit-tested in EditMode.
        /// </summary>
        public static bool IsAutoStartAllowedForMode(ConnectionMode mode)
            => mode == ConnectionMode.Custom;

        /// <summary>
        /// Starts the server if KeepServerRunning is enabled and no external server is detected.
        /// This method is called during Unity Editor startup to auto-start the server based on user preference.
        /// The external server check is performed asynchronously to avoid blocking the main thread.
        /// </summary>
        public static void StartServerIfNeeded()
        {
            EditorApplication.update -= StartServerIfNeeded;

            // Skip local server auto-start in Cloud mode — Unity connects to the cloud server instead
            if (!IsAutoStartAllowedForMode(UnityCopilotPluginEditor.ConnectionMode))
            {
                _logger.LogDebug("StartServerIfNeeded: Cloud mode active, skipping local server auto-start");
                return;
            }

            // Check if user wants the server to keep running
            if (!UnityCopilotPluginEditor.KeepServerRunning)
            {
                _logger.LogDebug("StartServerIfNeeded: KeepServerRunning is false, skipping auto-start");
                return;
            }

            // Check if server is already running (either local or detected from previous session)
            if (_serverStatus.CurrentValue == CopilotServerStatus.Running ||
                _serverStatus.CurrentValue == CopilotServerStatus.Starting)
            {
                _logger.LogDebug("StartServerIfNeeded: Server is already running or starting");
                return;
            }

            // Check if an external server is available on the port (non-blocking)
            var port = UnityCopilotPluginEditor.Port;
            CheckExternalServerAsync(port, externalAvailable =>
            {
                if (externalAvailable)
                {
                    _logger.LogInformation("StartServerIfNeeded: External server detected on port {port}, skipping local server start", port);
                    return;
                }

                // Start the local server
                _logger.LogInformation("StartServerIfNeeded: Starting local server (KeepServerRunning=true)");
                StartServer();
            });
        }

        /// <summary>
        /// Checks if an external server is listening on the given port on a background thread,
        /// then invokes the callback on the main thread with the result.
        /// </summary>
        static void CheckExternalServerAsync(int port, Action<bool> onResult)
        {
            Task.Run(() =>
            {
                var result = false;
                try
                {
                    using var client = new System.Net.Sockets.TcpClient();
                    var connectTask = client.ConnectAsync("localhost", port);
                    var completed = connectTask.Wait(500); // 500ms timeout

                    if (completed && client.Connected)
                    {
                        _logger.LogDebug("CheckExternalServerAsync: Port {port} is in use by another process", port);
                        result = true;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug("CheckExternalServerAsync: No server detected on port {port} ({message})", port, ex.Message);
                }
                return result;
            })
            .ContinueWith(task => onResult(task.Result), TaskScheduler.FromCurrentSynchronizationContext());
        }

        #endregion // Process Lifecycle
    }
}
