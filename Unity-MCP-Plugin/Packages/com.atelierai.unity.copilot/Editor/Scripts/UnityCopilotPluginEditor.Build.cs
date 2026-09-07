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
using com.AtelierAI.Unity.Copilot.Editor.Utils;
using Microsoft.Extensions.Logging;

namespace com.AtelierAI.Unity.Copilot
{
    public partial class UnityCopilotPluginEditor
    {
        /// <summary>
        /// Editor-only test accessor for the underlying <see cref="UnityConnectionConfig"/>.
        /// The field is <c>protected</c> on <see cref="UnityCopilotPlugin"/>; this accessor exposes
        /// it to the Editor test assembly (via <c>InternalsVisibleTo</c>) so tests can verify
        /// state that is written through the plugin's build path.
        /// </summary>
        internal UnityConnectionConfig ConnectionConfigForTests => unityConnectionConfig;

        public UnityCopilotPluginEditor BuildMcpPluginIfNeeded()
        {
            EditorOperationOwners.RegisterAndReconcile();
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var loggerProvider = BuildLoggerProvider();

            // Seed host project root into the connection config before building.
            unityConnectionConfig.ProjectRootPath = ProjectRootPath;
            _logger.LogTrace("Seeded ConnectionConfig.ProjectRootPath={path}", ProjectRootPath);

            // Phase B: advertise this Unity Editor's instance id so the MCP server can register
            // the connection in its instance registry and route per-session tool calls here.
            // The id is computed by UnityInstanceRegistry; safe to call here because
            // [InitializeOnLoad] already ran (UnityInstanceRegistry's static ctor writes
            // the entry on domain reload — before any plugin build is requested).
            try
            {
                unityConnectionConfig.InstanceId = UnityInstanceRegistry.BuildSelfEntry().InstanceId;
                _logger.LogTrace("Seeded ConnectionConfig.InstanceId={instanceId}", unityConnectionConfig.InstanceId);
            }
            catch (System.Exception ex)
            {
                // Discovery failures must never block the plugin from connecting.
                _logger.LogWarning("Failed to seed ConnectionConfig.InstanceId: {message}", ex.Message);
            }

            var built = _plugin.BuildOnce(() => BuildMcpPlugin(
                version: BuildVersion(),
                reflector: CreateDefaultReflector(),
                loggerProvider: loggerProvider,
                configure: builder => builder.WithToolExecutionScheduler(
                    EditorToolExecutionScheduler.Shared)
            ));
            stopwatch.Stop();

            if (built == null)
                return this; // already built, nothing to wire up

            _logger.LogDebug("Plugin built in {elapsedMilliseconds} ms.",
                stopwatch.ElapsedMilliseconds);

            SetCurrentPlugin(built);
            ApplyConfigToMcpPlugin(built);

            return this;
        }

        public void DisposeMcpPluginInstance()
        {
            var oldInstance = _plugin.TakeInstance();
            if (oldInstance == null)
                return;

            SetCurrentPlugin(null);

            // Dispose on a background thread to avoid blocking Unity's main thread.
            // The dispose path calls ConnectionManager.DisconnectImmediate() which
            // internally blocks on a semaphore (_gate) held by a pending Connect()
            // task whose continuation is queued on the main thread SynchronizationContext —
            // a deadlock if we block the main thread here.
            _ = System.Threading.Tasks.Task.Run(() => oldInstance.Dispose());
        }
    }
}
