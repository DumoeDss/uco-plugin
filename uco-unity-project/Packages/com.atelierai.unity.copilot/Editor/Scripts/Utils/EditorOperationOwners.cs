/*
┌──────────────────────────────────────────────────────────────────┐
│  Built-in ownership policies for long-running Editor operations. │
└──────────────────────────────────────────────────────────────────┘
*/

#nullable enable
using System;
using com.AtelierAI.Unity.Copilot.Editor.API;
using com.AtelierAI.Unity.Copilot.Editor.API.TestRunner;
using UnityEditor;

namespace com.AtelierAI.Unity.Copilot.Editor.Utils
{
    internal static class EditorOperationOwners
    {
        static bool s_registered;

        internal static void RegisterAndReconcile()
        {
            if (s_registered) return;

            EditorOperationRegistry.ConfigureInstanceIdProvider(
                () => UnityInstanceRegistry.BuildSelfEntry().InstanceId);
            EditorOperationOwnerRegistry.BeginRegistration();
            EditorOperationOwnerRegistry.Register(new BuildOwner());
            EditorOperationOwnerRegistry.Register(new TestsOwner());
            EditorOperationOwnerRegistry.Register(new RefreshOwner());
            EditorOperationOwnerRegistry.Register(new MenuOwner());
            EditorOperationOwnerRegistry.CompleteRegistrationAndReconcile();
            s_registered = true;
        }

        internal static bool ResetForTests()
        {
            s_registered = false;
            EditorOperationOwnerRegistry.BeginRegistration();
            return true;
        }

        sealed class BuildOwner : IEditorOperationOwner
        {
            public string Kind => "build-player";
            public string OwnerId => Kind;
            public EditorOperationReloadBehavior ReloadBehavior
                => EditorOperationReloadBehavior.Interrupt;

            public void Start(EditorOperationContext operation) { }

            public void RequestCancellation(EditorOperationContext operation, string reason)
                => operation.Update(
                    "build-cancellation-pending",
                    cancellationPending: true,
                    diagnosticsJson: "{\"reason\":\"non-interruptible-build\"}");

            public EditorOperationRecoveryResult Recover(EditorOperationContext operation)
                => EditorOperationRecoveryResult.Interrupt(
                    "build-interrupted-by-reload",
                    "A blocking BuildPipeline call cannot be resumed safely after reload.");
        }

        sealed class TestsOwner : IEditorOperationOwner
        {
            public string Kind => "tests-run";
            public string OwnerId => Kind;
            public EditorOperationReloadBehavior ReloadBehavior
                => EditorOperationReloadBehavior.Resume;

            public void Start(EditorOperationContext operation)
            {
                if (operation.Current?.Phase != "waiting-for-compilation") return;
                EditorApplication.update -= Tool_Tests.ResumePendingTestRunOnce;
                EditorApplication.update += Tool_Tests.ResumePendingTestRunOnce;
            }

            public void RequestCancellation(EditorOperationContext operation, string reason)
            {
                var diagnostic = Tool_Tests.RequestCancellation(
                    operation.OperationId, Tool_Tests.TestRunnerApi);
                operation.Update(
                    string.IsNullOrWhiteSpace(diagnostic) ? "cancelling" : "cancellation-pending",
                    cancellationPending: true,
                    diagnosticsJson: string.IsNullOrWhiteSpace(diagnostic)
                        ? null
                        : "{\"category\":\"test-runner-cancellation-unavailable\"}");
            }

            public EditorOperationRecoveryResult Recover(EditorOperationContext operation)
            {
                var current = operation.Current;
                if (current == null)
                    return EditorOperationRecoveryResult.Interrupt();

                if (current.Phase == "waiting-for-compilation")
                {
                    if (!Tool_Tests.TryClaimPendingTestRun(out var pendingId)
                        || !string.Equals(pendingId, operation.OperationId, StringComparison.Ordinal))
                    {
                        return EditorOperationRecoveryResult.Interrupt(
                            "tests-recovery-proof-missing",
                            "Persisted test discovery state does not match the operation.");
                    }
                    return EditorOperationRecoveryResult.Resume("waiting-for-compilation");
                }

                if (string.Equals(
                        TestResultCollector.TestOperationId.Value,
                        operation.OperationId,
                        StringComparison.Ordinal))
                {
                    return EditorOperationRecoveryResult.Resume(
                        current.Phase == "executing" ? "awaiting-test-runner-callback" : current.Phase);
                }

                return EditorOperationRecoveryResult.Interrupt(
                    "tests-recovery-proof-missing",
                    "The Unity Test Runner no longer proves ownership of this operation.");
            }
        }

        sealed class RefreshOwner : IEditorOperationOwner
        {
            public string Kind => "assets-refresh";
            public string OwnerId => Kind;
            public EditorOperationReloadBehavior ReloadBehavior
                => EditorOperationReloadBehavior.Resume;

            public void Start(EditorOperationContext operation)
            {
                if (operation.Current?.Phase == "waiting-for-compilation")
                    RefreshOperationReconciler.EnsureScheduled();
            }

            public void RequestCancellation(EditorOperationContext operation, string reason)
                => operation.Update(
                    "refresh-cancellation-pending",
                    cancellationPending: true,
                    diagnosticsJson: "{\"reason\":\"non-interruptible-refresh\"}");

            public EditorOperationRecoveryResult Recover(EditorOperationContext operation)
            {
                var persisted = SessionState.GetString(
                    Tool_Assets.PendingRefreshOperationKey, string.Empty);
                if (!string.Equals(persisted, operation.OperationId, StringComparison.Ordinal))
                {
                    return EditorOperationRecoveryResult.Interrupt(
                        "refresh-recovery-proof-missing",
                        "Persisted refresh ownership does not match the operation.");
                }

                return EditorOperationRecoveryResult.Resume("waiting-for-compilation");
            }
        }

        sealed class MenuOwner : IEditorOperationOwner
        {
            public string Kind => "editor-execute-menu-item";
            public string OwnerId => Kind;
            public EditorOperationReloadBehavior ReloadBehavior
                => EditorOperationReloadBehavior.Interrupt;

            public void Start(EditorOperationContext operation) { }

            public void RequestCancellation(EditorOperationContext operation, string reason)
                => operation.Update(
                    "menu-cancellation-pending",
                    cancellationPending: true,
                    diagnosticsJson: "{\"reason\":\"non-interruptible-menu-handler\"}");

            public EditorOperationRecoveryResult Recover(EditorOperationContext operation)
                => EditorOperationRecoveryResult.Interrupt(
                    "menu-interrupted-by-reload",
                    "An arbitrary menu handler cannot be replayed safely after reload.");
        }
    }
}
