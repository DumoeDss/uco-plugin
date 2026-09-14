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
using System.ComponentModel;
using System;
using System.Threading.Tasks;
using com.AtelierAI.Uco.Framework;
using com.AtelierAI.Uco.Framework.Common.Model;
using com.IvanMurzak.ReflectorNet.Utils;
using com.AtelierAI.Unity.Copilot.Editor.Utils;
using UnityEditor;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    public partial class Tool_Assets
    {
        public const string AssetsRefreshToolId = "assets-refresh";
        internal const string PendingRefreshOperationKey = "UnityCopilot.PendingRefreshOperation";
        [UcoTool
        (
            AssetsRefreshToolId,
            Title = "Assets / Refresh",
            IdempotentHint = true,
            DurableOperationStart = true
        )]
        [UcoSkillDescription("Refresh the Unity AssetDatabase. " +
            "Use after files were added or updated outside of the Unity API, or to force script recompilation " +
            "when a '.cs' file changed. Returns a processing/success response and waits for compilation when triggered.")]
        [UcoSkillBody("Refreshes the AssetDatabase. " +
            "Use it if any file was added or updated in the project outside of Unity API. " +
            "Use it if need to force scripts recompilation when '.cs' file changed.\n\n" +
            "## Inputs\n\n" +
            "- `options` — `ImportAssetOptions` flag (default `Default`; synchronous import is opt-in).\n" +
            "- `requestId` — optional compatibility correlation ID. The durable operation ID is authoritative.\n\n" +
            "## Behavior\n\n" +
            "Runs `AssetDatabase.Refresh(options)`. If `EditorApplication.isCompiling` is true after the refresh, " +
            "schedules a post-compilation notification and returns a `Processing` response. If compilation already " +
            "failed (`EditorUtility.scriptCompilationFailed`), returns `Success` with the compilation error details. " +
            "Otherwise returns a plain `Success`.")]
        [Description("Refreshes the AssetDatabase. " +
            "Use it if any file was added or updated in the project outside of Unity API. " +
            "Use it if need to force scripts recompilation when '.cs' file changed.")]
        public async Task<EditorOperationInfo> Refresh
        (
            [Description("Asset import options.")]
            ImportAssetOptions? options = ImportAssetOptions.Default,
            [RequestID]
            string? requestId = null
        )
        {
            return await MainThread.Instance.RunAsync(() =>
            {
                var effectiveOptions = options ?? ImportAssetOptions.Default;
                var operation = EditorOperationOwnerRegistry.Create(
                    "assets-refresh", "scheduled",
                    System.Text.Json.JsonSerializer.Serialize(new { options = effectiveOptions.ToString() }));
                EditorOperationOwnerRegistry.ScheduleExecution(
                    operation.OperationId, "asset-database-refresh",
                    () => RunRefresh(operation.OperationId, effectiveOptions));
                return operation;
            });
        }

        internal static void RunRefresh(string operationId, ImportAssetOptions options)
        {
            var operation = EditorOperationRegistry.Get(operationId);
            if (operation == null || operation.IsTerminal) return;
            if (operation.CancellationRequested)
            {
                EditorOperationRegistry.CancelRunning(operationId, "cancelled-before-refresh");
                return;
            }

            SessionState.SetString(PendingRefreshOperationKey, operationId);
            try
            {
                AssetDatabase.Refresh(options);

                if (EditorApplication.isCompiling)
                {
                    EditorOperationRegistry.Update(operationId, "waiting-for-compilation",
                        cancellationPending: EditorOperationRegistry.IsCancellationRequested(operationId));
                    // The exclusive section (the refresh itself) is over. Waiting for a
                    // possibly deferred compilation is Editor pacing, not execution:
                    // holding the serialized lane across it wedges every subsequent
                    // tool call for the lifetime of the deferral (EditMode batch runs
                    // defer compilation until exit). Recovery re-acquires on resume.
                    EditorOperationOwnerRegistry.TryReleaseExecution(operationId);
                    RefreshOperationReconciler.EnsureScheduled();
                    return;
                }

                if (EditorUtility.scriptCompilationFailed)
                {
                    var errorDetails = ScriptUtils.GetCompilationErrorDetails();
                    EditorOperationRegistry.Fail(operationId, "asset-refresh-compilation-failed",
                        errorDetails, "compilation-failed");
                    SessionState.EraseString(PendingRefreshOperationKey);
                    return;
                }

                if (EditorOperationRegistry.IsCancellationRequested(operationId))
                    EditorOperationRegistry.CancelRunning(operationId, "cancelled-after-refresh");
                else
                    EditorOperationRegistry.Succeed(operationId,
                        "{\"message\":\"AssetDatabase refreshed successfully.\"}");
                SessionState.EraseString(PendingRefreshOperationKey);
            }
            catch (Exception ex)
            {
                EditorOperationRegistry.Fail(operationId, "asset-refresh-failed", ex.Message, "refresh-failed");
                SessionState.EraseString(PendingRefreshOperationKey);
            }
        }
    }

    [InitializeOnLoad]
    internal static class RefreshOperationReconciler
    {
        static bool s_scheduled;

        static RefreshOperationReconciler()
        {
            // g-006 RefreshOwner validates and claims the persisted operation before
            // scheduling this poller; InitializeOnLoad must never race owner registration.
        }

        internal static void EnsureScheduled()
        {
            if (s_scheduled) return;
            s_scheduled = true;
            EditorApplication.update += Poll;
        }

        static void Poll()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            EditorApplication.update -= Poll;
            s_scheduled = false;

            var operationId = SessionState.GetString(Tool_Assets.PendingRefreshOperationKey, string.Empty);
            SessionState.EraseString(Tool_Assets.PendingRefreshOperationKey);
            if (string.IsNullOrEmpty(operationId)) return;
            var operation = EditorOperationRegistry.Get(operationId);
            if (operation == null || operation.IsTerminal) return;

            if (EditorUtility.scriptCompilationFailed)
                EditorOperationRegistry.Fail(operationId, "asset-refresh-compilation-failed",
                    ScriptUtils.GetCompilationErrorDetails(), "compilation-failed");
            else if (operation.CancellationRequested)
                EditorOperationRegistry.CancelRunning(operationId, "cancelled-after-refresh");
            else
                EditorOperationRegistry.Succeed(operationId,
                    "{\"message\":\"AssetDatabase refresh and compilation completed.\"}");
        }
    }
}
