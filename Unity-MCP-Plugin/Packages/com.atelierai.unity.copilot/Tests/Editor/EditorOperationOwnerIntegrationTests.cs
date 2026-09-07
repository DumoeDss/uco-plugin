#nullable enable
using System;
using System.IO;
using System.Linq;
using com.AtelierAI.Unity.Copilot.Editor.API;
using com.AtelierAI.Unity.Copilot.Editor.Utils;
using com.IvanMurzak.McpPlugin.Common.Model;
using NUnit.Framework;
using UnityEditor;

namespace com.AtelierAI.Unity.Copilot.Editor.Tests
{
    public sealed class EditorOperationOwnerIntegrationTests
    {
        string _directory = string.Empty;
        string _store = string.Empty;

        [SetUp]
        public void SetUp()
        {
            _directory = Path.Combine(Path.GetTempPath(),
                "unity-copilot-g006-owners-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            _store = Path.Combine(_directory, "operations.json");
            EditorOperationRegistry.ConfigureStoreForTests(_store);
            EditorOperationOwners.RegisterAndReconcile();
        }

        [TearDown]
        public void TearDown()
        {
            EditorOperationOwnerRegistry.InvalidateGeneration();
            EditorOperationRegistry.ConfigureStoreForTests(null);
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }

        [Test]
        public void BuildCompatibility_ProjectsOneSharedRecordAndPendingCancellation()
        {
            var succeeded = Tool_Build.BuildJobRegistry.Create(
                "StandaloneWindows64", "Build/fixture.exe");
            Tool_Build.BuildJobRegistry.Start(succeeded.JobId);
            Tool_Build.BuildJobRegistry.Complete(succeeded.JobId, 1.25, 42, 0, 1);
            var projected = Tool_Build.BuildJobRegistry.Get(succeeded.JobId)!;
            Assert.That(projected.Status, Is.EqualTo("succeeded"));
            Assert.That(projected.JobId, Is.EqualTo(succeeded.JobId));
            Assert.That(EditorOperationRegistry.Get(succeeded.JobId)!.Status,
                Is.EqualTo(projected.Status));

            var running = Tool_Build.BuildJobRegistry.Create(
                "StandaloneWindows64", "Build/cancel.exe");
            Tool_Build.BuildJobRegistry.Start(running.JobId);
            var pending = EditorOperationOwnerRegistry.RequestCancellation(running.JobId);
            Assert.That(pending.CancellationPending, Is.True);
            Tool_Build.BuildJobRegistry.CancelAfterBuildReturns(
                running.JobId, 0.5, 0, 0, 0);
            Assert.That(Tool_Build.BuildJobRegistry.Get(running.JobId)!.Status,
                Is.EqualTo("cancelled"));
        }

        [Test]
        public void TestCompatibility_QueriesAndCancelsTheSharedOperation()
        {
            var operation = EditorOperationOwnerRegistry.Create("tests-run", "queued");
            Assert.That(Tool_Tests.GetJob(operation.OperationId).OperationId,
                Is.EqualTo(operation.OperationId));
            Assert.That(Tool_Tests.ListJobs().Any(
                item => item.OperationId == operation.OperationId), Is.True);

            var cancelled = Tool_Tests.CancelJob(operation.OperationId);
            Assert.That(cancelled.Status, Is.EqualTo("cancelled"));
            Assert.That(cancelled.CancellationRequested, Is.True);
        }

        [Test]
        public void RefreshAndMenu_CancelBeforeStartAndNeverReplay()
        {
            var refresh = EditorOperationOwnerRegistry.Create(
                "assets-refresh", "scheduled");
            EditorOperationOwnerRegistry.RequestCancellation(refresh.OperationId);
            Tool_Assets.RunRefresh(refresh.OperationId, ImportAssetOptions.Default);
            Assert.That(EditorOperationRegistry.Get(refresh.OperationId)!.Status,
                Is.EqualTo("cancelled"));

            var calls = 0;
            var menu = EditorOperationOwnerRegistry.Create(
                "editor-execute-menu-item", "scheduled");
            EditorOperationOwnerRegistry.RequestCancellation(menu.OperationId);
            Tool_Editor.RunMenuItem(menu.OperationId, "File/Save", _ =>
            {
                calls++;
                return true;
            });
            Assert.That(calls, Is.Zero);
            Assert.That(EditorOperationRegistry.Get(menu.OperationId)!.Status,
                Is.EqualTo("cancelled"));
        }

        [Test]
        public void MenuCancellationDuringNonInterruptibleHandlerWinsWithObservedResult()
        {
            var operation = EditorOperationOwnerRegistry.Create(
                "editor-execute-menu-item", "scheduled");
            EditorOperationRegistry.Start(
                operation.OperationId, "executing-non-interruptible-menu-handler");
            Tool_Editor.RunMenuItem(operation.OperationId, "Fixture/Long Handler", _ =>
            {
                EditorOperationRegistry.RequestCancellation(operation.OperationId);
                return true;
            });

            var cancelled = EditorOperationRegistry.Get(operation.OperationId)!;
            Assert.That(cancelled.Status, Is.EqualTo("cancelled"));
            Assert.That(cancelled.Phase,
                Is.EqualTo("cancelled-after-non-interruptible-menu-handler"));
            Assert.That(cancelled.ResultJson, Does.Contain("\"Ok\":true"));
        }

        [Test]
        public void GenericCancel_UsesOwnerDispatchAndReturnsStableNotFound()
        {
            var tool = new Tool_Operations();
            var operation = EditorOperationOwnerRegistry.Create("tests-run", "queued");
            Assert.That(tool.Cancel(operation.OperationId).Status, Is.EqualTo("cancelled"));

            var missing = Assert.Throws<ToolCallControlException>(
                () => tool.Cancel("missing-operation"));
            Assert.That(missing!.Code, Is.EqualTo(
                com.IvanMurzak.McpPlugin.Common.Model.ToolCallErrorCodes.OperationNotFound));
        }
    }
}
