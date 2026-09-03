#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using com.AtelierAI.Unity.Copilot.Editor.API;
using com.AtelierAI.Unity.Copilot.Editor.Utils;
using com.IvanMurzak.McpPlugin;
using com.IvanMurzak.McpPlugin.Common.Model;
using com.IvanMurzak.ReflectorNet;
using com.IvanMurzak.ReflectorNet.Utils;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace com.AtelierAI.Unity.Copilot.Editor.Tests
{
    /// <summary>
    /// g-005 pilot verification against the real Editor: dry-run invariants
    /// (4.5), one-step Undo and truthful reporting (5.5), failure/abort
    /// behaviour (5.6), the production batch entry point (6.4), plan
    /// staleness/reload invalidation, and no-bypass for direct runner calls.
    /// Every call goes through the production tool manager unless the test
    /// is explicitly proving that a bypass attempt fails.
    /// </summary>
    [TestFixture]
    public sealed class AuthoringSafetyPilotTests : BaseTest
    {
        private const string TmpFolderName = "AuthoringSafetyTests_TMP";
        private const string TmpFolder = "Assets/" + TmpFolderName;
        private static readonly BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;

        private bool _ignoreFailingMessages;

        public override IEnumerator SetUp()
        {
            yield return base.SetUp();
            UnityCopilotPluginEditor.Instance.BuildMcpPluginIfNeeded();
            // Rejections are logged as errors by the manager ("Error Response
            // to AI"); the assertions here are on responses and Editor state.
            _ignoreFailingMessages = LogAssert.ignoreFailingMessages;
            LogAssert.ignoreFailingMessages = true;
        }

        public override IEnumerator TearDown()
        {
            LogAssert.ignoreFailingMessages = _ignoreFailingMessages;
            if (AssetDatabase.IsValidFolder(TmpFolder))
                AssetDatabase.DeleteAsset(TmpFolder);
            yield return base.TearDown();
        }

        // ------------------------------------------------------------------
        // 4.5 dry-run invariants
        // ------------------------------------------------------------------

        [UnityTest]
        public IEnumerator DryRun_ValidateAndPlan_LeaveEditorUntouched()
        {
            EnsureTmpFolder();
            var host = new GameObject("dry-run-host");
            host.AddComponent<BoxCollider>();
            var parent = new GameObject("dry-run-parent");
            var hostId = ObjectIdJsonValue(host);
            var parentId = ObjectIdJsonValue(parent);

            var calls = new (string Tool, string Json)[]
            {
                (Tool_GameObject.GameObjectCreateToolId, @"{""name"":""dry-run-created""}"),
                (Tool_GameObject.GameObjectDestroyToolId, $@"{{""gameObjectRef"":{{""instanceID"":{hostId}}}}}"),
                (Tool_GameObject.GameObjectComponentAddToolId, $@"{{""componentNames"":[""UnityEngine.SphereCollider""],""gameObjectRef"":{{""instanceID"":{hostId}}}}}"),
                (Tool_GameObject.GameObjectComponentDestroyToolId, $@"{{""gameObjectRef"":{{""instanceID"":{hostId}}},""destroyComponentRefs"":[{{""typeName"":""UnityEngine.BoxCollider""}}]}}"),
                (Tool_GameObject.GameObjectSetParentToolId, $@"{{""gameObjectRefs"":[{{""instanceID"":{hostId}}}],""parentGameObjectRef"":{{""instanceID"":{parentId}}}}}"),
                (Tool_GameObject.GameObjectDuplicateToolId, $@"{{""gameObjectRefs"":[{{""instanceID"":{hostId}}}]}}"),
                (Tool_Scene.SceneCreateToolId, $@"{{""path"":""{TmpFolder}/dry-run.unity""}}"),
                (Tool_Scene.SceneSaveToolId, $@"{{""path"":""{TmpFolder}/dry-run-save.unity""}}"),
            };

            var before = EditorSnapshot.Capture(TmpFolder);
            foreach (var (tool, json) in calls)
            {
                foreach (var mode in new[] { "validate", "plan" })
                {
                    ResponseData<ResponseCallTool>? response = null;
                    yield return Call(tool, json, Control(tool + "-" + mode, dryRun: mode), r => response = r);
                    Assert.IsNotNull(response, tool);
                    Assert.AreEqual(ResponseStatus.Success, response!.Status, $"{tool} {mode}: {response.Message}");
                    var result = response.Value!.StructuredContent!["result"]!;
                    Assert.AreEqual(mode, result["dryRun"]!.GetValue<string>(), tool);
                    Assert.IsTrue(result["valid"]!.GetValue<bool>(), $"{tool} {mode} should validate");
                    Assert.IsNull(response.Value.Transaction, $"{tool} {mode} must not open a transaction");
                    if (mode == "plan")
                    {
                        var plan = result["confirmationPlan"]!;
                        StringAssert.StartsWith("plan-", plan["planId"]!.GetValue<string>());
                        StringAssert.StartsWith("sha256-", plan["planHash"]!.GetValue<string>());
                        var targets = plan["targets"]!.AsArray();
                        Assert.Greater(targets.Count, 0, $"{tool} plan should list targets");
                        foreach (var target in targets)
                            StringAssert.StartsWith("sha256-", target!["fingerprint"]!.GetValue<string>(), tool);
                        Assert.Greater(plan["predictedEffects"]!.AsArray().Count, 0, tool);
                    }
                    else
                    {
                        Assert.IsNull(result["confirmationPlan"], $"{tool} validate must not issue a plan");
                    }
                    EditorSnapshot.Capture(TmpFolder).AssertEquals(before, $"{tool} {mode}");
                }
            }
        }

        [UnityTest]
        public IEnumerator Validate_ReportsMissingTargetWithoutMutation()
        {
            var before = EditorSnapshot.Capture(null);
            ResponseData<ResponseCallTool>? response = null;
            yield return Call(
                Tool_GameObject.GameObjectDestroyToolId,
                @"{""gameObjectRef"":{""instanceID"":123456789}}",
                Control("missing-target", dryRun: "validate"),
                r => response = r);

            Assert.AreEqual(ResponseStatus.Error, response!.Status);
            var error = response.StructuredError ?? response.Value?.StructuredError;
            Assert.AreEqual("validation_failed", error!.Code);
            Assert.AreEqual("target_not_found", error.Details!["reason"]!.GetValue<string>());
            EditorSnapshot.Capture(null).AssertEquals(before, "invalid validate");
        }

        // ------------------------------------------------------------------
        // Confirmation lifecycle against real targets
        // ------------------------------------------------------------------

        [UnityTest]
        public IEnumerator Destroy_LegacyIsRejected_PlanIsStaleAfterTargetChange_ThenConfirmedDestroyIsUndoable()
        {
            var victim = new GameObject("victim");
            var id = ObjectIdJsonValue(victim);
            var json = $@"{{""gameObjectRef"":{{""instanceID"":{id}}}}}";

            // Legacy destructive call: no mutation, copyable retry instruction.
            ResponseData<ResponseCallTool>? legacy = null;
            yield return Call(Tool_GameObject.GameObjectDestroyToolId, json, null, r => legacy = r);
            Assert.AreEqual(ResponseStatus.Error, legacy!.Status);
            StringAssert.Contains("[" + ToolCallErrorCodes.ConfirmationRequired + "]", legacy.Value!.GetMessage());
            StringAssert.Contains("confirmationPlan", legacy.Value.GetMessage());
            StringAssert.Contains("retryWith", legacy.Value.GetMessage());
            Assert.IsNotNull(GameObject.Find("victim"), "a rejected legacy destroy must not mutate");

            // Plan, then change the target: the approval is stale.
            ResponseData<ResponseCallTool>? planned = null;
            yield return Call(Tool_GameObject.GameObjectDestroyToolId, json, Control("destroy", dryRun: "plan"), r => planned = r);
            var token = ReadPlan(planned!);
            victim.name = "victim-renamed";
            ResponseData<ResponseCallTool>? stale = null;
            yield return Call(Tool_GameObject.GameObjectDestroyToolId, json, Control("destroy", confirmation: token), r => stale = r);
            Assert.AreEqual(ToolCallErrorCodes.ConfirmationStale, ErrorCode(stale!));
            Assert.IsNotNull(GameObject.Find("victim-renamed"));

            // Fresh plan on the current target executes once and is undoable.
            ResponseData<ResponseCallTool>? replanned = null;
            yield return Call(Tool_GameObject.GameObjectDestroyToolId, json, Control("destroy-2", dryRun: "plan"), r => replanned = r);
            var freshToken = ReadPlan(replanned!);
            ResponseData<ResponseCallTool>? executed = null;
            yield return Call(Tool_GameObject.GameObjectDestroyToolId, json, Control("destroy-2", confirmation: freshToken), r => executed = r);
            Assert.AreEqual(ResponseStatus.Success, executed!.Status, executed.Message);
            Assert.IsNull(GameObject.Find("victim-renamed"), "confirmed destroy must destroy");
            var transaction = executed.Value!.Transaction!;
            Assert.AreEqual("full", transaction["undo"]!.GetValue<string>());
            Assert.IsTrue(transaction["mutated"]!.GetValue<bool>());
            Assert.IsTrue(transaction["completed"]!.GetValue<bool>());
            Assert.AreEqual("MCP: " + Tool_GameObject.GameObjectDestroyToolId, transaction["groupLabel"]!.GetValue<string>());

            // A replayed token is not a standing capability.
            var again = new GameObject("victim-again");
            ResponseData<ResponseCallTool>? replay = null;
            yield return Call(Tool_GameObject.GameObjectDestroyToolId, $@"{{""gameObjectRef"":{{""instanceID"":{ObjectIdJsonValue(again)}}}}}", Control("destroy-2", confirmation: freshToken), r => replay = r);
            Assert.AreEqual(ResponseStatus.Error, replay!.Status);
            Assert.IsNotNull(GameObject.Find("victim-again"));

            Undo.PerformUndo();
            Assert.IsNotNull(GameObject.Find("victim-renamed"), "one Undo step must restore the destroyed object");
        }

        [UnityTest]
        public IEnumerator ReloadGuard_InvalidatesIssuedPlans()
        {
            var victim = new GameObject("reload-victim");
            var json = $@"{{""gameObjectRef"":{{""instanceID"":{ObjectIdJsonValue(victim)}}}}}";
            ResponseData<ResponseCallTool>? planned = null;
            yield return Call(Tool_GameObject.GameObjectDestroyToolId, json, Control("reload", dryRun: "plan"), r => planned = r);
            var token = ReadPlan(planned!);

            Assert.GreaterOrEqual(UnityAuthoringSafetyReloadGuard.InvalidateConfirmationPlans(), 1,
                "the live pipeline must expose at least one confirmation store");

            ResponseData<ResponseCallTool>? afterReload = null;
            yield return Call(Tool_GameObject.GameObjectDestroyToolId, json, Control("reload", confirmation: token), r => afterReload = r);
            Assert.AreEqual(ToolCallErrorCodes.ConfirmationInvalid, ErrorCode(afterReload!));
            Assert.IsNotNull(GameObject.Find("reload-victim"));
        }

        // ------------------------------------------------------------------
        // 5.5 one-step Undo, dirty state, full versus partial
        // ------------------------------------------------------------------

        [UnityTest]
        public IEnumerator Create_IsOneUndoStepWithFullReport()
        {
            var scene = SceneManager.GetActiveScene();
            var groupBefore = Undo.GetCurrentGroup();
            ResponseData<ResponseCallTool>? response = null;
            yield return Call(Tool_GameObject.GameObjectCreateToolId, @"{""name"":""undo-created""}", Control("create"), r => response = r);

            Assert.AreEqual(ResponseStatus.Success, response!.Status, response.Message);
            var created = GameObject.Find("undo-created");
            Assert.IsNotNull(created);
            Assert.IsTrue(scene.isDirty || !scene.IsValid() || string.IsNullOrEmpty(scene.path),
                "an executing create marks the active scene dirty");
            var transaction = response.Value!.Transaction!;
            Assert.AreEqual("full", transaction["undo"]!.GetValue<string>());
            Assert.AreEqual("MCP: " + Tool_GameObject.GameObjectCreateToolId, transaction["groupLabel"]!.GetValue<string>());
            Assert.IsTrue(transaction["mutated"]!.GetValue<bool>());
            Assert.AreEqual("none", transaction["rollback"]!.GetValue<string>());
            Assert.Greater(transaction["affectedObjects"]!.AsArray().Count, 0);
            Assert.Greater(Undo.GetCurrentGroup(), groupBefore, "the call must own a new Undo group");

            Undo.PerformUndo();
            Assert.IsNull(GameObject.Find("undo-created"), "one Undo step removes the created object");
        }

        [UnityTest]
        public IEnumerator ComponentAddAndSetParent_AreOneUndoStepEach()
        {
            var host = new GameObject("undo-host");
            var parent = new GameObject("undo-parent");
            var hostId = ObjectIdJsonValue(host);

            ResponseData<ResponseCallTool>? add = null;
            yield return Call(
                Tool_GameObject.GameObjectComponentAddToolId,
                $@"{{""componentNames"":[""UnityEngine.BoxCollider""],""gameObjectRef"":{{""instanceID"":{hostId}}}}}",
                Control("add"),
                r => add = r);
            Assert.AreEqual(ResponseStatus.Success, add!.Status, add.Message);
            Assert.IsNotNull(host.GetComponent<BoxCollider>());
            Assert.AreEqual("full", add.Value!.Transaction!["undo"]!.GetValue<string>());
            Undo.PerformUndo();
            Assert.IsNull(host.GetComponent<BoxCollider>(), "one Undo step removes the added component");

            ResponseData<ResponseCallTool>? reparent = null;
            yield return Call(
                Tool_GameObject.GameObjectSetParentToolId,
                $@"{{""gameObjectRefs"":[{{""instanceID"":{hostId}}}],""parentGameObjectRef"":{{""instanceID"":{ObjectIdJsonValue(parent)}}}}}",
                Control("parent"),
                r => reparent = r);
            Assert.AreEqual(ResponseStatus.Success, reparent!.Status, reparent.Message);
            Assert.AreEqual(parent.transform, host.transform.parent);
            Assert.AreEqual("full", reparent.Value!.Transaction!["undo"]!.GetValue<string>());
            Undo.PerformUndo();
            Assert.IsNull(host.transform.parent, "one Undo step restores the previous parent");
        }

        [UnityTest]
        public IEnumerator ComponentDestroy_UsesUndoAwareDestroyAndIsUndoable()
        {
            var host = new GameObject("component-destroy-host");
            host.AddComponent<BoxCollider>();
            ResponseData<ResponseCallTool>? planned = null;
            var json = $@"{{""gameObjectRef"":{{""instanceID"":{ObjectIdJsonValue(host)}}},""destroyComponentRefs"":[{{""typeName"":""UnityEngine.BoxCollider""}}]}}";
            yield return Call(Tool_GameObject.GameObjectComponentDestroyToolId, json, Control("cdestroy", dryRun: "plan"), r => planned = r);
            ResponseData<ResponseCallTool>? executed = null;
            yield return Call(Tool_GameObject.GameObjectComponentDestroyToolId, json, Control("cdestroy", confirmation: ReadPlan(planned!)), r => executed = r);

            Assert.AreEqual(ResponseStatus.Success, executed!.Status, executed.Message);
            Assert.IsNull(host.GetComponent<BoxCollider>());
            Assert.AreEqual("full", executed.Value!.Transaction!["undo"]!.GetValue<string>());
            Undo.PerformUndo();
            Assert.IsNotNull(host.GetComponent<BoxCollider>(), "Undo.DestroyObjectImmediate makes the deletion undoable");
        }

        [UnityTest]
        public IEnumerator Modify_ReportsPartialUndoTruthfully()
        {
            var target = new GameObject("modify-me");
            var arguments = new Dictionary<string, JsonElement>
            {
                ["gameObjectRefs"] = JsonSerializer.SerializeToElement(new[] { new { instanceID = ObjectIdWireValue(target) } }),
                ["jsonPatchesPerGameObject"] = JsonSerializer.SerializeToElement(new[] { "{\"name\":\"modified-name\"}" }),
            };
            ResponseData<ResponseCallTool>? response = null;
            yield return Call(Tool_GameObject.GameObjectModifyToolId, arguments, Control("modify"), r => response = r);

            Assert.AreEqual(ResponseStatus.Success, response!.Status, response.Message);
            Assert.AreEqual("modified-name", target.name);
            var transaction = response.Value!.Transaction!;
            Assert.AreEqual("partial", transaction["undo"]!.GetValue<string>(), "reflected patches are advertised as partial");
            Assert.IsTrue(transaction["mutated"]!.GetValue<bool>());
            Undo.PerformUndo();
            Assert.AreEqual("modify-me", target.name, "the recorded complete snapshot restores the name");
        }

        // ------------------------------------------------------------------
        // 5.6 failure/abort and no-bypass
        // ------------------------------------------------------------------

        [UnityTest]
        public IEnumerator DirectRunnerInvocation_FailsClosedWithoutMutation()
        {
            var manager = UnityCopilotPluginEditor.Instance.Tools!;
            var runner = manager.GetAllTools().First(tool => tool.Name == Tool_GameObject.GameObjectCreateToolId);
            var before = EditorSnapshot.Capture(null);

            LogAssert.ignoreFailingMessages = true;
            var direct = runner.Run("direct-bypass", new Dictionary<string, JsonElement>
            {
                ["name"] = JsonSerializer.SerializeToElement("bypass-created"),
            });
            yield return WaitForTask(direct);

            Assert.AreEqual(ResponseStatus.Error, direct.Result.Status, "a direct runner call must fail closed");
            Assert.IsNull(GameObject.Find("bypass-created"), "no object may be created outside the policy pipeline");
            EditorSnapshot.Capture(null).AssertEquals(before, "direct bypass");
        }

        [UnityTest]
        public IEnumerator RunnerFailureAfterMutation_RevertsOnlyItsGroup()
        {
            var originalInstance = UnityCopilotPluginEditor.Instance;
            originalInstance.BuildMcpPluginIfNeeded();
            var originalPlugin = originalInstance.McpPluginInstance;
            var failing = new FailingCreateRunner();
            var replacement = new TestUnityCopilotPluginEditor(failing);
            SetEditorSingleton(replacement);
            try
            {
                replacement.BuildMcpPluginIfNeeded();
                var manager = replacement.Tools!;

                // Unrelated earlier history that must survive the abort.
                var unrelated = new GameObject("unrelated-history");
                Undo.RegisterCreatedObjectUndo(unrelated, "unrelated");
                Undo.IncrementCurrentGroup();

                var request = new RequestCallTool("failing-request", failing.Name, new Dictionary<string, JsonElement>(), Control("failing"));
                LogAssert.ignoreFailingMessages = true;
                var execution = manager.RunCallTool(request);
                yield return WaitForTask(execution);
                var response = execution.Result;

                Assert.AreEqual(ResponseStatus.Error, response.Status);
                Assert.IsNull(GameObject.Find(FailingCreateRunner.VictimName), "the aborted group reverts the created object");
                Assert.IsNotNull(GameObject.Find("unrelated-history"), "unrelated Undo history is preserved");
                var transaction = response.Value!.Transaction!;
                Assert.IsTrue(transaction["aborted"]!.GetValue<bool>());
                Assert.IsFalse(transaction["completed"]!.GetValue<bool>());
                Assert.AreEqual("complete", transaction["rollback"]!.GetValue<string>());
                Assert.AreEqual(1, failing.Invocations);
            }
            finally
            {
                RestoreEditorSingleton(originalInstance, originalPlugin, replacement);
            }
        }

        // ------------------------------------------------------------------
        // 6.4 production batch with pilot children
        // ------------------------------------------------------------------

        [UnityTest]
        public IEnumerator Batch_PilotChildren_CollapseToOneUndoStep()
        {
            var commands = new[]
            {
                new Tool_Batch.BatchCommand { Tool = Tool_GameObject.GameObjectCreateToolId, Params = Params(("name", "batch-a")) },
                new Tool_Batch.BatchCommand { Tool = Tool_GameObject.GameObjectCreateToolId, Params = Params(("name", "batch-b")) },
            };
            var arguments = new Dictionary<string, JsonElement>
            {
                ["commands"] = JsonSerializer.SerializeToElement(commands),
                ["failFast"] = JsonSerializer.SerializeToElement(true),
            };

            ResponseData<ResponseCallTool>? planned = null;
            yield return Call(Tool_Batch.BatchExecuteToolId, arguments, Control("batch", dryRun: "plan"), r => planned = r);
            Assert.AreEqual(ResponseStatus.Success, planned!.Status, planned.Message);
            Assert.IsNull(GameObject.Find("batch-a"), "planning a batch must not create children");
            var plan = planned.Value!.StructuredContent!["result"]!["confirmationPlan"]!;
            Assert.GreaterOrEqual(plan["targets"]!.AsArray().Count, 2);

            ResponseData<ResponseCallTool>? executed = null;
            yield return Call(Tool_Batch.BatchExecuteToolId, arguments, Control("batch", confirmation: ReadPlan(planned)), r => executed = r);
            Assert.AreEqual(ResponseStatus.Success, executed!.Status, executed.Message);
            var batchResult = JsonSerializer.Deserialize<Tool_Batch.BatchResult>(
                executed.Value!.StructuredContent!["result"]!.ToJsonString())!;
            Assert.AreEqual(2, batchResult.Succeeded, string.Join("; ", batchResult.Results.Select(r => r.Error)));
            Assert.IsNotNull(GameObject.Find("batch-a"));
            Assert.IsNotNull(GameObject.Find("batch-b"));
            var transaction = executed.Value.Transaction!;
            Assert.IsTrue(transaction["completed"]!.GetValue<bool>());
            Assert.IsTrue(transaction["mutated"]!.GetValue<bool>());
            Assert.AreEqual("full", transaction["undo"]!.GetValue<string>(), "two fully recorded children keep aggregate full Undo");
            Assert.AreEqual(2, transaction["affectedObjects"]!.AsArray().Count, "both children attach to the parent group");
            for (var index = 0; index < batchResult.Results.Length; index++)
            {
                var child = batchResult.Results[index];
                Assert.IsTrue(child.Ok);
                Assert.IsNotNull(child.Transaction, "each shared child reports its own transaction contribution");
                Assert.IsTrue(child.Transaction!["shared"]!.GetValue<bool>());
                Assert.IsFalse(child.Transaction["pending"]!.GetValue<bool>());
                Assert.IsTrue(child.Transaction["completed"]!.GetValue<bool>());
                Assert.IsFalse(child.Transaction["aborted"]!.GetValue<bool>());
                Assert.AreEqual("none", child.Transaction["rollback"]!.GetValue<string>());
                Assert.AreEqual("full", child.Transaction["undo"]!.GetValue<string>());
                Assert.AreEqual(transaction["groupId"]!.GetValue<int>(), child.Transaction["groupId"]!.GetValue<int>());
                Assert.AreEqual(transaction["groupLabel"]!.GetValue<string>(), child.Transaction["groupLabel"]!.GetValue<string>());
                var affectedNames = child.Transaction["affectedObjects"]!.AsArray()
                    .Select(value => value!["name"]!.GetValue<string>())
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                CollectionAssert.AreEqual(new[] { index == 0 ? "batch-a" : "batch-b" }, affectedNames,
                    "a child report may retain multiple recording events, but only for that child's object");
            }

            Undo.PerformUndo();
            Assert.IsNull(GameObject.Find("batch-a"), "one Undo step reverts the whole batch");
            Assert.IsNull(GameObject.Find("batch-b"));
        }

        [UnityTest]
        public IEnumerator Batch_SharedChildFailure_RevertsPriorChildAndFinalizesTruthfulResults()
        {
            var originalInstance = UnityCopilotPluginEditor.Instance;
            originalInstance.BuildMcpPluginIfNeeded();
            var originalPlugin = originalInstance.McpPluginInstance;
            var failing = new FailingCreateRunner();
            var replacement = new TestUnityCopilotPluginEditor(failing);
            SetEditorSingleton(replacement);
            try
            {
                replacement.BuildMcpPluginIfNeeded();
                var unrelated = new GameObject("batch-unrelated-history");
                Undo.RegisterCreatedObjectUndo(unrelated, "batch unrelated");
                Undo.IncrementCurrentGroup();

                var commands = new[]
                {
                    new Tool_Batch.BatchCommand
                    {
                        Tool = Tool_GameObject.GameObjectCreateToolId,
                        Params = Params(("name", "batch-shared-restored")),
                    },
                    new Tool_Batch.BatchCommand
                    {
                        Tool = failing.Name,
                        Params = new Dictionary<string, JsonElement>(),
                    },
                    new Tool_Batch.BatchCommand
                    {
                        Tool = Tool_GameObject.GameObjectCreateToolId,
                        Params = Params(("name", "batch-shared-skipped")),
                    },
                };
                var arguments = new Dictionary<string, JsonElement>
                {
                    ["commands"] = JsonSerializer.SerializeToElement(commands),
                    ["failFast"] = JsonSerializer.SerializeToElement(true),
                };

                ResponseData<ResponseCallTool>? planned = null;
                yield return Call(Tool_Batch.BatchExecuteToolId, arguments,
                    Control("batch-shared-failure", dryRun: "plan"), r => planned = r);
                Assert.AreEqual(ResponseStatus.Success, planned!.Status, planned.Message);

                ResponseData<ResponseCallTool>? executed = null;
                yield return Call(Tool_Batch.BatchExecuteToolId, arguments,
                    Control("batch-shared-failure", confirmation: ReadPlan(planned)), r => executed = r);
                Assert.AreEqual(ResponseStatus.Success, executed!.Status, executed.Message);
                var result = JsonSerializer.Deserialize<Tool_Batch.BatchResult>(
                    executed.Value!.StructuredContent!["result"]!.ToJsonString())!;

                Assert.AreEqual(0, result.Succeeded);
                Assert.AreEqual(2, result.Failed);
                Assert.IsTrue(result.Aborted);
                Assert.IsNull(GameObject.Find("batch-shared-restored"),
                    "the parent abort restores a prior successful shared child");
                Assert.IsNull(GameObject.Find(FailingCreateRunner.VictimName),
                    "the failing child's mutation is restored by the same parent group");
                Assert.IsNull(GameObject.Find("batch-shared-skipped"));
                Assert.IsNotNull(GameObject.Find("batch-unrelated-history"),
                    "aborting the batch must preserve unrelated Undo history");

                var prior = result.Results[0];
                Assert.IsFalse(prior.Ok, "a rolled-back child must not remain successful");
                Assert.IsFalse(prior.Skipped);
                Assert.AreEqual(ToolCallErrorCodes.AuthoringTransactionFailed, prior.ErrorCode);
                Assert.IsNull(prior.Data);
                Assert.IsNotNull(prior.Transaction);
                Assert.IsTrue(prior.Transaction!["shared"]!.GetValue<bool>());
                Assert.IsFalse(prior.Transaction["pending"]!.GetValue<bool>());
                Assert.IsTrue(prior.Transaction["aborted"]!.GetValue<bool>());
                Assert.IsFalse(prior.Transaction["completed"]!.GetValue<bool>());
                Assert.AreEqual("complete", prior.Transaction["rollback"]!.GetValue<string>());
                Assert.AreEqual("full", prior.Transaction["undo"]!.GetValue<string>());
                var priorAffectedNames = prior.Transaction["affectedObjects"]!.AsArray()
                    .Select(value => value!["name"]!.GetValue<string>())
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                CollectionAssert.AreEqual(new[] { "batch-shared-restored" }, priorAffectedNames,
                    "the rolled-back child report may contain multiple record events, but only for its object");

                var failed = result.Results[1];
                Assert.IsFalse(failed.Ok);
                Assert.IsFalse(failed.Skipped);
                Assert.AreEqual(ToolCallErrorCodes.ToolExecutionFailed, failed.ErrorCode,
                    "the failing child retains its original runtime error");
                Assert.IsNotNull(failed.Transaction);
                Assert.IsTrue(failed.Transaction!["aborted"]!.GetValue<bool>());
                Assert.AreEqual("complete", failed.Transaction["rollback"]!.GetValue<string>());
                Assert.IsTrue(result.Results[2].Skipped);
                Assert.IsFalse(result.Results[2].Ok);
            }
            finally
            {
                RestoreEditorSingleton(originalInstance, originalPlugin, replacement);
            }
        }

        [UnityTest]
        public IEnumerator Batch_PartialSharedChildFailure_ReportsUncertainFinalStateAndRetainsContext()
        {
            var originalInstance = UnityCopilotPluginEditor.Instance;
            originalInstance.BuildMcpPluginIfNeeded();
            var originalPlugin = originalInstance.McpPluginInstance;
            var failing = new FailingCreateRunner();
            var replacement = new TestUnityCopilotPluginEditor(failing);
            SetEditorSingleton(replacement);
            try
            {
                replacement.BuildMcpPluginIfNeeded();
                var target = new GameObject("batch-shared-partial");
                var unrelated = new GameObject("batch-partial-unrelated-history");
                Undo.RegisterCreatedObjectUndo(unrelated, "batch partial unrelated");
                Undo.IncrementCurrentGroup();

                var commands = new[]
                {
                    new Tool_Batch.BatchCommand
                    {
                        Tool = Tool_GameObject.GameObjectModifyToolId,
                        Params = Params(
                            ("gameObjectRefs", new[] { new { instanceID = ObjectIdWireValue(target) } }),
                            ("jsonPatchesPerGameObject", new[] { "{\"name\":\"batch-shared-partial-modified\"}" })),
                    },
                    new Tool_Batch.BatchCommand
                    {
                        Tool = failing.Name,
                        Params = new Dictionary<string, JsonElement>(),
                    },
                    new Tool_Batch.BatchCommand
                    {
                        Tool = Tool_GameObject.GameObjectCreateToolId,
                        Params = Params(("name", "batch-partial-skipped")),
                    },
                };
                var arguments = new Dictionary<string, JsonElement>
                {
                    ["commands"] = JsonSerializer.SerializeToElement(commands),
                    ["failFast"] = JsonSerializer.SerializeToElement(true),
                };

                ResponseData<ResponseCallTool>? planned = null;
                yield return Call(Tool_Batch.BatchExecuteToolId, arguments,
                    Control("batch-partial-failure", dryRun: "plan"), r => planned = r);
                Assert.AreEqual(ResponseStatus.Success, planned!.Status, planned.Message);

                ResponseData<ResponseCallTool>? executed = null;
                yield return Call(Tool_Batch.BatchExecuteToolId, arguments,
                    Control("batch-partial-failure", confirmation: ReadPlan(planned)), r => executed = r);
                Assert.AreEqual(ResponseStatus.Success, executed!.Status, executed.Message);
                var result = JsonSerializer.Deserialize<Tool_Batch.BatchResult>(
                    executed.Value!.StructuredContent!["result"]!.ToJsonString())!;

                Assert.AreEqual(0, result.Succeeded);
                Assert.AreEqual(2, result.Failed);
                Assert.IsTrue(result.Aborted);
                Assert.IsNotNull(GameObject.Find("batch-partial-unrelated-history"),
                    "aborting a partial batch must preserve unrelated Undo history");
                Assert.IsNull(GameObject.Find("batch-partial-skipped"));

                var parentTransaction = executed.Value.Transaction!;
                Assert.IsTrue(parentTransaction["aborted"]!.GetValue<bool>());
                Assert.IsFalse(parentTransaction["completed"]!.GetValue<bool>());
                Assert.AreEqual("partial", parentTransaction["rollback"]!.GetValue<string>());

                var prior = result.Results[0];
                Assert.IsFalse(prior.Ok, "an aborted prior mutation must not remain successful");
                Assert.IsFalse(prior.Skipped);
                Assert.AreEqual(ToolCallErrorCodes.AuthoringTransactionFailed, prior.ErrorCode);
                StringAssert.Contains("final mutation state is uncertain", prior.Error);
                StringAssert.DoesNotContain("did not remain applied", prior.Error);
                Assert.IsNotNull(prior.Data, "uncertain rollback retains bounded child diagnostics");
                Assert.LessOrEqual(prior.Data!.ToString()!.Length, 1024);
                Assert.IsNotNull(prior.Transaction);
                Assert.IsTrue(prior.Transaction!["shared"]!.GetValue<bool>());
                Assert.IsFalse(prior.Transaction["pending"]!.GetValue<bool>());
                Assert.IsTrue(prior.Transaction["aborted"]!.GetValue<bool>());
                Assert.IsFalse(prior.Transaction["completed"]!.GetValue<bool>());
                Assert.AreEqual("partial", prior.Transaction["rollback"]!.GetValue<string>());
                Assert.AreEqual("partial", prior.Transaction["undo"]!.GetValue<string>());
                var priorAffectedNames = prior.Transaction["affectedObjects"]!.AsArray()
                    .Select(value => value!["name"]!.GetValue<string>())
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                CollectionAssert.Contains(priorAffectedNames, "batch-shared-partial");

                var failed = result.Results[1];
                Assert.IsFalse(failed.Ok);
                Assert.IsFalse(failed.Skipped);
                Assert.AreEqual(ToolCallErrorCodes.ToolExecutionFailed, failed.ErrorCode,
                    "the failing child retains its original runtime error");
                Assert.AreEqual("Tool execution failed.", failed.Error,
                    "parent finalization must not replace the failing child's original safe error");
                Assert.IsNotNull(failed.Transaction);
                Assert.IsTrue(failed.Transaction!["shared"]!.GetValue<bool>());
                Assert.IsFalse(failed.Transaction["pending"]!.GetValue<bool>());
                Assert.IsTrue(failed.Transaction["aborted"]!.GetValue<bool>());
                Assert.AreEqual("partial", failed.Transaction["rollback"]!.GetValue<string>());

                Assert.IsTrue(result.Results[2].Skipped);
                Assert.IsFalse(result.Results[2].Ok);
            }
            finally
            {
                RestoreEditorSingleton(originalInstance, originalPlugin, replacement);
            }
        }

        [UnityTest]
        public IEnumerator Batch_AllowsUndoNoneAndRejectsInvalidChildBeforeAnyMutation()
        {
            EnsureTmpFolder();
            var undoNone = new Dictionary<string, JsonElement>
            {
                ["commands"] = JsonSerializer.SerializeToElement(new[]
                {
                    new Tool_Batch.BatchCommand { Tool = Tool_GameObject.GameObjectCreateToolId, Params = Params(("name", "batch-undo-none")) },
                    new Tool_Batch.BatchCommand { Tool = Tool_Scene.SceneSaveToolId, Params = Params(("path", $"{TmpFolder}/batch-undo-none.unity")) },
                }),
            };
            ResponseData<ResponseCallTool>? undoNonePlan = null;
            yield return Call(Tool_Batch.BatchExecuteToolId, undoNone, Control("batch-undo-none", dryRun: "plan"), r => undoNonePlan = r);
            Assert.AreEqual(ResponseStatus.Success, undoNonePlan!.Status, undoNonePlan.Message);
            var aggregatePlan = undoNonePlan.Value!.StructuredContent!["result"]!["confirmationPlan"]!;
            Assert.AreEqual("none", aggregatePlan["undo"]!.GetValue<string>());
            Assert.IsNull(GameObject.Find("batch-undo-none"), "planning must not execute the first child");

            var before = EditorSnapshot.Capture(null);
            var missingTarget = new Dictionary<string, JsonElement>
            {
                ["commands"] = JsonSerializer.SerializeToElement(new[]
                {
                    new Tool_Batch.BatchCommand { Tool = Tool_GameObject.GameObjectCreateToolId, Params = Params(("name", "batch-rejected-2")) },
                    new Tool_Batch.BatchCommand
                    {
                        Tool = Tool_GameObject.GameObjectDestroyToolId,
                        Params = new Dictionary<string, JsonElement>
                        {
                            ["gameObjectRef"] = JsonSerializer.SerializeToElement(new { instanceID = 123456789 }),
                        },
                    },
                }),
            };
            ResponseData<ResponseCallTool>? missingPlan = null;
            yield return Call(Tool_Batch.BatchExecuteToolId, missingTarget, Control("batch-missing", dryRun: "plan"), r => missingPlan = r);
            Assert.AreEqual(ResponseStatus.Error, missingPlan!.Status);
            Assert.IsNull(GameObject.Find("batch-rejected-2"));

            // A confirmed batch is still rejected by preflight when a child
            // cannot be planned at execution time (its target vanished).
            var victim = new GameObject("batch-victim");
            var vanishing = new Dictionary<string, JsonElement>
            {
                ["commands"] = JsonSerializer.SerializeToElement(new[]
                {
                    new Tool_Batch.BatchCommand
                    {
                        Tool = Tool_GameObject.GameObjectDestroyToolId,
                        Params = new Dictionary<string, JsonElement>
                        {
                            ["gameObjectRef"] = JsonSerializer.SerializeToElement(new { instanceID = ObjectIdWireValue(victim) }),
                        },
                    },
                    new Tool_Batch.BatchCommand { Tool = Tool_GameObject.GameObjectCreateToolId, Params = Params(("name", "batch-rejected-3")) },
                }),
            };
            ResponseData<ResponseCallTool>? vanishingPlan = null;
            yield return Call(Tool_Batch.BatchExecuteToolId, vanishing, Control("batch-vanish", dryRun: "plan"), r => vanishingPlan = r);
            Assert.AreEqual(ResponseStatus.Success, vanishingPlan!.Status, vanishingPlan.Message);
            UnityEngine.Object.DestroyImmediate(victim);
            ResponseData<ResponseCallTool>? vanishingExec = null;
            yield return Call(Tool_Batch.BatchExecuteToolId, vanishing, Control("batch-vanish", confirmation: ReadPlan(vanishingPlan)), r => vanishingExec = r);
            Assert.AreEqual(ResponseStatus.Error, vanishingExec!.Status, "the parent plan is stale once a child target vanished");
            Assert.IsNull(GameObject.Find("batch-rejected-3"), "no child may run when the aggregate approval fails");

            EditorSnapshot.Capture(null).AssertEquals(before, "rejected batches");
        }

        // ------------------------------------------------------------------
        // 5.5 static regressions: direct DestroyImmediate / mutation-before-recording
        // ------------------------------------------------------------------

        [Test]
        public void PilotSources_NeverUseDirectDestroyImmediateOrMutateBeforeRecording()
        {
            var toolDirectory = Path.GetFullPath(Path.Combine("Packages", "com.atelierai.unity.copilot", "Editor", "Scripts", "API", "Tool"));
            Assert.IsTrue(Directory.Exists(toolDirectory), toolDirectory);
            var pilotFiles = Directory.GetFiles(toolDirectory, "*.cs")
                .Where(file =>
                {
                    var name = Path.GetFileName(file);
                    return name.StartsWith("GameObject.", StringComparison.Ordinal)
                        || name.StartsWith("Scene.", StringComparison.Ordinal);
                })
                .Where(file => File.ReadAllText(file).Contains("[AuthoringCapability("))
                .ToArray();
            Assert.GreaterOrEqual(pilotFiles.Length, 13, "expected the Scene/GameObject/Component pilot files");

            var directDestroy = new Regex(@"(?<!Undo\.)\bDestroyImmediate\s*\(");
            foreach (var file in pilotFiles)
            {
                var source = File.ReadAllText(file);
                Assert.IsFalse(directDestroy.IsMatch(source), $"{Path.GetFileName(file)} must not call a non-Undo DestroyImmediate");
                StringAssert.Contains("UnityAuthoringUndo.RequireAuthoringScope()", source, Path.GetFileName(file));
                var scopeIndex = source.IndexOf("UnityAuthoringUndo.RequireAuthoringScope()", StringComparison.Ordinal);
                var markIndex = source.IndexOf("UnityAuthoringUndo.MarkMutated()", StringComparison.Ordinal);
                if (markIndex >= 0)
                {
                    Assert.Less(scopeIndex, markIndex, $"{Path.GetFileName(file)}: scope check must precede the mutation mark");
                    var recordIndex = new[]
                    {
                        source.IndexOf("UnityAuthoringUndo.RecordCreated(", StringComparison.Ordinal),
                        source.IndexOf("UnityAuthoringUndo.RecordModified(", StringComparison.Ordinal),
                        source.IndexOf("UnityAuthoringUndo.RecordHostRegistered(", StringComparison.Ordinal),
                        source.IndexOf("UnityAuthoringUndo.AddComponent(", StringComparison.Ordinal),
                        source.IndexOf("UnityAuthoringUndo.SetParent(", StringComparison.Ordinal),
                        source.IndexOf("UnityAuthoringUndo.Destroy(", StringComparison.Ordinal),
                    }.Where(index => index >= 0).DefaultIfEmpty(-1).Min();
                    var declaresUndo = source.Contains("UndoLevel = AuthoringUndoLevel.Full") || source.Contains("UndoLevel = AuthoringUndoLevel.Partial");
                    if (declaresUndo)
                        Assert.IsTrue(recordIndex >= 0 && recordIndex < markIndex, $"{Path.GetFileName(file)}: an undoable pilot tool must record before marking a mutation");
                }
            }

            // The read-only inspection region must not reach a side-effecting Unity API.
            var adapters = File.ReadAllText(Path.GetFullPath(Path.Combine("Packages", "com.atelierai.unity.copilot", "Editor", "Scripts", "Utils", "UnityAuthoringAdapters.cs")));
            var start = adapters.IndexOf("internal static class UnityPilotAuthoringInspection", StringComparison.Ordinal);
            var end = adapters.IndexOf("public sealed class UnityBatchAuthoringValidator", StringComparison.Ordinal);
            Assert.IsTrue(start >= 0 && end > start, "inspection region not found");
            var inspection = adapters.Substring(start, end - start);
            foreach (var forbidden in new[] { "SaveAssets", "CreateAsset", "DestroyImmediate", "new GameObject(", "AddComponent<", "SetDirty", "MarkSceneDirty", "SaveScene", "NewScene(", "OpenScene(", "Undo." })
                Assert.IsFalse(inspection.Contains(forbidden), $"read-only inspection must not use {forbidden}");
        }

        // ------------------------------------------------------------------
        // helpers
        // ------------------------------------------------------------------

        private static void EnsureTmpFolder()
        {
            if (!AssetDatabase.IsValidFolder(TmpFolder))
                AssetDatabase.CreateFolder("Assets", TmpFolderName);
        }

        private static Dictionary<string, JsonElement> Params(params (string Name, object Value)[] values)
        {
            var result = new Dictionary<string, JsonElement>();
            foreach (var (name, value) in values)
                result[name] = JsonSerializer.SerializeToElement(value);
            return result;
        }

        private static object ObjectIdWireValue(UnityEngine.Object value)
        {
#if UNITY_6000_5_OR_NEWER
            return UnityEngine.EntityId.ToULong(value.GetEntityId())
                .ToString(System.Globalization.CultureInfo.InvariantCulture);
#else
            return value.GetInstanceID();
#endif
        }

        private static string ObjectIdJsonValue(UnityEngine.Object value)
            => JsonSerializer.Serialize(ObjectIdWireValue(value));

        private static string ObjectIdText(UnityEngine.Object value)
            => Convert.ToString(ObjectIdWireValue(value), System.Globalization.CultureInfo.InvariantCulture)
                ?? "0";

        private static ToolCallControl Control(string callId, string? dryRun = null, ToolCallConfirmation? confirmation = null)
            => new ToolCallControl
            {
                Version = ToolCallControl.CurrentVersion,
                CallId = callId,
                CorrelationId = callId + "-trace",
                DryRun = dryRun,
                Confirm = confirmation == null ? (bool?)null : true,
                Confirmation = confirmation,
            };

        private static ToolCallConfirmation ReadPlan(ResponseData<ResponseCallTool> response)
        {
            Assert.AreEqual(ResponseStatus.Success, response.Status, "plan: " + response.Message);
            var plan = response.Value!.StructuredContent!["result"]!["confirmationPlan"]!.AsObject();
            return new ToolCallConfirmation
            {
                PlanId = plan["planId"]!.GetValue<string>(),
                PlanHash = plan["planHash"]!.GetValue<string>(),
                ExpiresAtUnixMs = plan["expiresAtUnixMs"]!.GetValue<long>(),
            };
        }

        private static string? ErrorCode(ResponseData<ResponseCallTool> response)
            => response.StructuredError?.Code ?? response.Value?.StructuredError?.Code;

        private IEnumerator Call(string tool, string json, ToolCallControl? control, Action<ResponseData<ResponseCallTool>> onDone)
            => Call(tool, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!, control, onDone);

        private IEnumerator Call(
            string tool,
            Dictionary<string, JsonElement> arguments,
            ToolCallControl? control,
            Action<ResponseData<ResponseCallTool>> onDone)
        {
            // requestID is part of the confirmation binding: derive it from the
            // call id so plan and execution share one logical identity.
            var requestId = (control?.CallId ?? "legacy-" + Guid.NewGuid().ToString("N")) + "-request";
            var request = control == null
                ? new RequestCallTool(requestId, tool, arguments)
                : new RequestCallTool(requestId, tool, arguments, control);
            // Unity resets this between SetUp and the test body; rejections
            // are logged as errors by the manager and asserted on the response.
            LogAssert.ignoreFailingMessages = true;
            var task = UnityCopilotPluginEditor.Instance.Tools!.RunCallTool(request);
            yield return WaitForTask(task);
            onDone(task.Result);
        }

        private static void SetEditorSingleton(UnityCopilotPluginEditor replacement)
        {
            var instanceField = typeof(UnityCopilotPluginEditor).GetField("instance", PrivateStatic);
            Assert.IsNotNull(instanceField, "Editor singleton field was not found.");
            instanceField!.SetValue(null, replacement);
        }

        private static void RestoreEditorSingleton(
            UnityCopilotPluginEditor original,
            IMcpPlugin? originalPlugin,
            TestUnityCopilotPluginEditor replacement)
        {
            replacement.DisposeMcpPluginInstance();
            replacement.Dispose();
            var instanceField = typeof(UnityCopilotPluginEditor).GetField("instance", PrivateStatic);
            instanceField!.SetValue(null, original);
            var setCurrentPlugin = typeof(UnityCopilotPluginEditor).GetMethod("SetCurrentPlugin", PrivateStatic);
            setCurrentPlugin!.Invoke(null, new object?[] { originalPlugin });
        }

        /// <summary>Bounded view of Editor state that a dry-run must not change.</summary>
        private sealed class EditorSnapshot
        {
            public string Objects = string.Empty;
            public string Dirty = string.Empty;
            public int UndoGroup;
            public string Files = string.Empty;

            public static EditorSnapshot Capture(string? watchFolder)
            {
                var ids = new List<string>();
                var dirty = new List<string>();
                for (var index = 0; index < SceneManager.sceneCount; index++)
                {
                    var scene = SceneManager.GetSceneAt(index);
                    if (!scene.IsValid() || !scene.isLoaded)
                        continue;
                    dirty.Add(scene.name + "=" + scene.isDirty);
                    foreach (var root in scene.GetRootGameObjects())
                    {
                        foreach (var transform in root.GetComponentsInChildren<Transform>(true))
                        {
                            var go = transform.gameObject;
                            ids.Add(ObjectIdText(go) + ":" + go.name + ":" + go.GetComponents<Component>().Length + ":" + (go.transform.parent == null ? "0" : ObjectIdText(go.transform.parent)));
                        }
                    }
                }
                ids.Sort(StringComparer.Ordinal);

                var files = string.Empty;
                if (watchFolder != null)
                {
                    var full = Path.GetFullPath(watchFolder);
                    files = Directory.Exists(full)
                        ? string.Join("|", Directory.GetFiles(full, "*", SearchOption.AllDirectories)
                            .OrderBy(path => path, StringComparer.Ordinal)
                            .Select(path => Path.GetFileName(path) + ":" + new FileInfo(path).Length))
                        : "<missing>";
                }

                return new EditorSnapshot
                {
                    Objects = string.Join("|", ids),
                    Dirty = string.Join("|", dirty),
                    UndoGroup = Undo.GetCurrentGroup(),
                    Files = files,
                };
            }

            public void AssertEquals(EditorSnapshot expected, string context)
            {
                Assert.AreEqual(expected.Objects, Objects, context + ": object set/state changed");
                Assert.AreEqual(expected.Dirty, Dirty, context + ": scene dirty flags changed");
                Assert.AreEqual(expected.UndoGroup, UndoGroup, context + ": Undo group changed");
                Assert.AreEqual(expected.Files, Files, context + ": project files changed");
            }
        }

        private sealed class TestUnityCopilotPluginEditor : UnityCopilotPluginEditor
        {
            private readonly IReadOnlyList<IRunTool> _runners;

            public TestUnityCopilotPluginEditor(params IRunTool[] runners)
            {
                _runners = runners;
                ConnectionConfigForTests.KeepConnected = false;
            }

            protected override IMcpPlugin BuildMcpPlugin(
                com.IvanMurzak.McpPlugin.Common.Version version,
                Reflector reflector,
                ILoggerProvider? loggerProvider = null,
                Action<IMcpPluginBuilder>? configure = null)
            {
                return base.BuildMcpPlugin(
                    version,
                    reflector,
                    loggerProvider,
                    builder =>
                    {
                        configure?.Invoke(builder);
                        foreach (var runner in _runners)
                            builder.AddTool(runner.Name, runner);
                    });
            }
        }

        /// <summary>
        /// Test-only mutating runner: creates a real GameObject inside the
        /// ambient transaction and then throws, modelling a tool that fails
        /// after Unity accepted a mutation.
        /// </summary>
        private sealed class FailingCreateRunner : IRunTool
        {
            public const string VictimName = "tx-victim";

            public string Name => "test-failing-create";
            public bool Enabled { get; set; } = true;
            public string? Title => Name;
            public string? Description => "Test-only failing authoring runner.";
            public MethodInfo? Method => null;
            public string? SkillDescription => null;
            public string? SkillBody => null;
            public JsonNode? InputSchema => new JsonObject();
            public JsonNode? OutputSchema => null;
            public McpToolType ToolType => McpToolType.Standard;
            public bool? ReadOnlyHint => false;
            public bool? DestructiveHint => null;
            public bool? IdempotentHint => null;
            public bool? OpenWorldHint => null;
            public AuthoringCapabilityDescriptor? AuthoringCapability { get; } = new AuthoringCapabilityDescriptor
            {
                MutationKind = AuthoringMutationKind.Create,
                UndoLevel = AuthoringUndoLevel.Full,
                SupportsValidation = true,
                SupportsPlanning = true,
                Validator = new UnityPilotAuthoringValidator(),
                Planner = new UnityPilotAuthoringPlanner(),
                TransactionFactory = new UnityAuthoringTransactionFactory(),
            };
            public int TokenCount => 0;
            public int Invocations { get; private set; }

            public Task<ResponseCallTool> Run(
                string requestId,
                IReadOnlyDictionary<string, JsonElement>? namedParameters,
                CancellationToken cancellationToken = default)
            {
                Invocations++;
                return Task.FromResult(MainThread.Instance.Run(() =>
                {
                    UnityAuthoringUndo.RequireAuthoringScope();
                    var victim = new GameObject(VictimName);
                    UnityAuthoringUndo.RecordCreated(victim);
                    UnityAuthoringUndo.MarkMutated();
                    throw new InvalidOperationException("failure after mutation");
#pragma warning disable CS0162
                    return ResponseCallTool.Success().SetRequestID(requestId);
#pragma warning restore CS0162
                }));
            }
        }
    }
}
