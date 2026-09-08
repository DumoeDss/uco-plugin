#nullable enable
using System;
using System.Linq;
using System.Threading.Tasks;
using com.AtelierAI.Unity.Copilot.Editor.API;
using com.AtelierAI.Unity.Copilot.Editor.Utils;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace com.AtelierAI.Unity.Copilot.Editor.Tests
{
    /// <summary>
    /// COCli-06 contract tests: the sandbox scene isolates script/test side
    /// effects, restoration reports its outcome (including the
    /// reload-interrupted case), the dirty-user-scene blocker still applies in
    /// sandbox mode, and the mutated flag tracks observed scene state.
    /// </summary>
    public class SceneSandboxHygieneTests
    {
        string _tempScenePath = string.Empty;

        [SetUp]
        public void EnsureCleanScene()
        {
            // The sandbox opens an additive untitled scene, which Unity refuses
            // while the active scene is itself untitled — so every test here
            // runs against a SAVED scratch scene (the normal user setup).
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            _tempScenePath = "Assets/SceneSandboxHygieneTests_"
                + Guid.NewGuid().ToString("N").Substring(0, 8) + ".unity";
            EditorSceneManager.SaveScene(scene, _tempScenePath);
        }

        [TearDown]
        public void RestoreCleanScene()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            if (_tempScenePath.Length > 0 && System.IO.File.Exists(_tempScenePath))
            {
                AssetDatabase.DeleteAsset(_tempScenePath);
                _tempScenePath = string.Empty;
            }
        }

        [Test]
        public void Sandbox_CreatesUntitledScene_AndRestoresSetupSelectionAndDirtyState()
        {
            var capture = EditorSceneSandbox.CaptureState();
            var baselineSelection = Selection.instanceIDs;
            var sceneCountBefore = EditorSceneManager.sceneCount;

            var sandbox = EditorSceneSandbox.OpenSandboxScene();
            Assert.That(EditorSceneManager.sceneCount, Is.EqualTo(sceneCountBefore + 1));
            Assert.That(SceneManager.GetActiveScene().handle, Is.EqualTo(sandbox.handle));
            Assert.That(sandbox.path, Is.Empty);

            // Probe dirties the sandbox scene and changes the selection — the
            // exact side effects the sandbox exists to contain.
            var probe = new GameObject("SandboxProbe");
            Selection.activeObject = probe;

            var (restored, cause) = EditorSceneSandbox.RestoreCapturedState(capture, sandbox);

            Assert.That(restored, Is.True, cause ?? string.Empty);
            Assert.That(EditorSceneManager.sceneCount, Is.EqualTo(sceneCountBefore));
            // Sandbox objects are gone with the scene (destroyed Unity objects
            // compare equal to null through the overloaded == operator).
            Assert.That(probe == null, Is.True);
            // Active scene and selection are back to the captured state.
            Assert.That(Selection.instanceIDs, Is.EqualTo(baselineSelection));
            var active = SceneManager.GetActiveScene();
            Assert.That(active.handle, Is.Not.EqualTo(sandbox.handle));
            // The user's scene was never dirtied by the sandboxed probe.
            Assert.That(EditorSceneManager.GetSceneAt(0).isDirty, Is.False);
        }

        [Test]
        public void DirtyUserScene_StillBlocksTestsRun_InSandboxMode()
        {
            // A dirty user scene must block exactly as without sandbox mode;
            // sandbox exempts only the (not yet opened) sandbox scene.
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            Assert.That(SceneManager.GetActiveScene().isDirty, Is.True);

            InvalidOperationException? caught = null;
            try
            {
                // Main-thread invocation completes synchronously in EditMode.
                Tool_Tests.Run(testMode: TestMode.EditMode, sandboxScene: true)
                    .GetAwaiter().GetResult();
            }
            catch (InvalidOperationException ex)
            {
                caught = ex;
            }
            Assert.That(caught, Is.Not.Null,
                "the dirty-scene blocker must fire in sandbox mode too");
            Assert.That(caught!.Message, Does.Contain("unsaved changes"));
        }

        [Test]
        public void UntitledActiveScene_RejectsTheSandboxWithAnActionableError()
        {
            // Back to an untitled scene: the sandbox precondition fires before
            // any scene state changes.
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var caught = Assert.Throws<InvalidOperationException>(
                () => EditorSceneSandbox.OpenSandboxScene());
            Assert.That(caught!.Message, Does.Contain("requires the active scene to be saved"));
            // Nothing was opened or dirtied by the failed attempt.
            Assert.That(EditorSceneManager.sceneCount, Is.EqualTo(1));
        }

        [Test]
        public void ReloadInterruptedSandbox_CaptureUnreadable_ReportsFailedRestoration()
        {
            const string operationId = "op-reload-sim";
            TestRunSceneSandbox.PersistForOperation(
                operationId,
                EditorSceneSandbox.CaptureState(),
                EditorSceneSandbox.CaptureMutationBaseline());
            // Simulate a reload that corrupts the persisted capture.
            SessionState.SetString("UnityCopilot.TestSandbox." + operationId, "{not-json");

            var outcome = TestRunSceneSandbox.RestoreForOperation(operationId);

            Assert.That(outcome.HadContext, Is.True);
            Assert.That(outcome.SandboxRestored, Is.False);
            Assert.That(outcome.SandboxRestoreCause, Is.Not.Null);
            // The failed restoration is consumed exactly once.
            Assert.That(TestRunSceneSandbox.HasContext(operationId), Is.False);
        }

        [Test]
        public void MissingCapture_AfterEditorRestart_ReportsCleanNoContextOutcome()
        {
            var outcome = TestRunSceneSandbox.RestoreForOperation("op-never-persisted");
            Assert.That(outcome.HadContext, Is.False);
            Assert.That(outcome.SandboxRestored, Is.Null);
            Assert.That(outcome.Mutation.Mutated, Is.False);
        }

        [Test]
        public void MutatedFlag_ReadOnlyProbeReportsNone_DirtyingProbeReportsMutated()
        {
            // Read-only: no scene touched, no selection change.
            var baseline = EditorSceneSandbox.CaptureMutationBaseline();
            _ = new GameObject("TransientSelection"); // created, never selected
            var readOnly = EditorSceneSandbox.DiffMutation(baseline);
            Assert.That(readOnly.Mutated, Is.False,
                "creating an object alone does not dirty a scene or change selection");

            // Dirtying probe: mark the active scene dirty.
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            var dirtying = EditorSceneSandbox.DiffMutation(baseline);
            Assert.That(dirtying.Mutated, Is.True);
            Assert.That(dirtying.MutatedScenes, Has.Length.EqualTo(1));

            // Selection change alone also reports a mutation.
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var selectionBaseline = EditorSceneSandbox.CaptureMutationBaseline();
            var picked = new GameObject("Picked");
            Selection.activeObject = picked;
            var selectionReport = EditorSceneSandbox.DiffMutation(selectionBaseline);
            Assert.That(selectionReport.Mutated, Is.True);
        }

        [Test]
        public void ScriptExecute_ReportsMutationWhenNonSandboxProbeDirtiesScene()
        {
            // Object creation alone does not reliably mark the scene dirty in
            // batchmode; the probe marks it explicitly — exactly the "read-only
            // probe that dirties a scene as a side effect" case the flag exists
            // to catch.
            var result = Tool_Script.Execute(
                csharpCode: "var go = new UnityEngine.GameObject(\"NonSandboxProbe\"); UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(UnityEngine.SceneManagement.SceneManager.GetActiveScene());",
                className: "Script",
                methodName: "Main",
                isMethodBody: true);

            Assert.That(result.Mutated, Is.True);
            Assert.That(result.MutatedScenes, Has.Length.EqualTo(1));
            Assert.That(result.SandboxUsed, Is.Null);
        }

        [Test]
        public void ScriptExecute_SandboxRestoresSceneAndReportsCleanMutation()
        {
            var result = Tool_Script.Execute(
                csharpCode: "var go = new UnityEngine.GameObject(\"SandboxedProbe\"); UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(UnityEngine.SceneManagement.SceneManager.GetActiveScene());",
                className: "Script",
                methodName: "Main",
                isMethodBody: true,
                sandboxScene: true);

            Assert.That(result.SandboxUsed, Is.True);
            Assert.That(result.SandboxRestored, Is.True, result.SandboxRestoreCause ?? string.Empty);
            // The sandbox scene (and its dirt) is gone; the user's scene state
            // was never touched, so the observed mutation is clean.
            Assert.That(result.Mutated, Is.False);
            Assert.That(result.MutatedScenes, Is.Empty);
            Assert.That(EditorSceneManager.sceneCount, Is.EqualTo(1));
        }

        [Test]
        public void ScriptExecute_FailingSandboxedProbeStillRestoresTheScene()
        {
            var sceneCountBefore = EditorSceneManager.sceneCount;
            try
            {
                _ = Tool_Script.Execute(
                    csharpCode: "throw new System.InvalidOperationException(\"sandbox probe failure\");",
                    className: "Script",
                    methodName: "Main",
                    isMethodBody: true,
                    sandboxScene: true);
                Assert.Fail("The throwing probe should have surfaced its exception.");
            }
            catch (Exception ex)
            {
                Assert.That(ex.Message, Does.Contain("sandbox probe failure"));
            }

            // The restore ran in the failure path: no sandbox scene is left open.
            Assert.That(EditorSceneManager.sceneCount, Is.EqualTo(sceneCountBefore));
        }
    }
}
