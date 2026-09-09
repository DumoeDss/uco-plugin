/*
┌──────────────────────────────────────────────────────────────────┐
│  Disposable scene sandbox and scene-mutation observation (g-C06)  │
└──────────────────────────────────────────────────────────────────┘
*/

#nullable enable
#if !UNITY_6000_5_OR_NEWER
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace com.AtelierAI.Unity.Copilot.Editor.Utils
{
    /// <summary>
    /// Observed open-scene state captured on the main thread: the manager
    /// setup (paths, loaded, active), the Editor selection, and per-scene
    /// dirty flags. Untitled scenes are identified by runtime handle because
    /// their path is empty.
    /// </summary>
    internal sealed class SceneStateCapture
    {
        public SceneSetupEntry[] Setup = Array.Empty<SceneSetupEntry>();
        public int[] SelectionInstanceIds = Array.Empty<int>();
        public int[] DirtySceneHandles = Array.Empty<int>();

        public sealed class SceneSetupEntry
        {
            public string Path = string.Empty;
            public bool IsLoaded;
            public bool IsActive;
        }
    }

    /// <summary>
    /// Minimal baseline for the <c>mutated</c> flag: active scene identity,
    /// selection identity, and the set of dirty scenes (by handle + path).
    /// </summary>
    internal sealed class SceneMutationBaseline
    {
        public int ActiveSceneHandle;
        public int[] SelectionInstanceIds = Array.Empty<int>();
        public int[] DirtySceneHandles = Array.Empty<int>();
    }

    internal readonly struct SceneMutationReport
    {
        public SceneMutationReport(bool mutated, string[] scenes)
        {
            Mutated = mutated;
            MutatedScenes = scenes;
        }

        public bool Mutated { get; }
        public string[] MutatedScenes { get; }

        public static readonly SceneMutationReport None = new(false, Array.Empty<string>());
    }

    /// <summary>
    /// Scene hygiene primitives shared by script-execute and tests-run:
    /// capture/restore of the user's Editor scene state, the opt-in sandbox
    /// scene, and mutation observation computed from observed scene state —
    /// never from guesses about what the executed code did.
    /// </summary>
    internal static class EditorSceneSandbox
    {
        /// <summary>Capture the full restorable scene state. Main thread only.</summary>
        public static SceneStateCapture CaptureState()
        {
            var setup = EditorSceneManager.GetSceneManagerSetup();
            var capture = new SceneStateCapture
            {
                Setup = setup == null
                    ? Array.Empty<SceneStateCapture.SceneSetupEntry>()
                    : setup.Select(entry => new SceneStateCapture.SceneSetupEntry
                    {
                        Path = entry.path ?? string.Empty,
                        IsLoaded = entry.isLoaded,
                        IsActive = entry.isActive,
                    }).ToArray(),
                SelectionInstanceIds = (int[])Selection.instanceIDs,
                DirtySceneHandles = OpenScenes()
                    .Where(scene => scene.isDirty)
                    .Select(scene => scene.handle)
                    .ToArray(),
            };
            return capture;
        }

        /// <summary>Capture the mutation-comparison baseline. Main thread only.</summary>
        public static SceneMutationBaseline CaptureMutationBaseline()
            => new()
            {
                ActiveSceneHandle = SceneManager.GetActiveScene().handle,
                SelectionInstanceIds = (int[])Selection.instanceIDs,
                DirtySceneHandles = OpenScenes()
                    .Where(scene => scene.isDirty)
                    .Select(scene => scene.handle)
                    .ToArray(),
            };

        /// <summary>
        /// Diff observed scene state against the baseline: any open scene left
        /// dirty (newly dirty or still dirty), a changed active scene, or a
        /// changed selection reports a mutation with the affected scenes named.
        /// </summary>
        public static SceneMutationReport DiffMutation(SceneMutationBaseline baseline)
        {
            if (baseline == null) return SceneMutationReport.None;

            var mutatedScenes = new List<string>();
            foreach (var scene in OpenScenes())
            {
                if (scene.isDirty && !baseline.DirtySceneHandles.Contains(scene.handle))
                {
                    mutatedScenes.Add(scene.path.Length > 0 ? scene.path : "Untitled");
                }
            }
            var activeChanged = SceneManager.GetActiveScene().handle != baseline.ActiveSceneHandle;
            var selectionChanged = !Selection.instanceIDs.SequenceEqual(baseline.SelectionInstanceIds);

            var mutated = mutatedScenes.Count > 0 || activeChanged || selectionChanged;
            return mutated
                ? new SceneMutationReport(true, mutatedScenes.ToArray())
                : SceneMutationReport.None;
        }

        /// <summary>
        /// Open a new untitled sandbox scene additively and make it active.
        /// The user's setup stays loaded and untouched until restore. Unity
        /// refuses to create an additive scene while the active scene is an
        /// unsaved untitled scene, so that precondition is rejected up front
        /// with an actionable message instead of Unity's raw error.
        /// </summary>
        public static Scene OpenSandboxScene()
        {
            var active = SceneManager.GetActiveScene();
            if (active.path.Length == 0)
            {
                throw new InvalidOperationException(
                    "sandboxScene requires the active scene to be saved: Unity cannot open " +
                    "the additive sandbox scene while an untitled scene is active. Save the " +
                    "scene (scene-save) and retry.");
            }

            var sandbox = EditorSceneManager.NewScene(
                NewSceneSetup.DefaultGameObjects, NewSceneMode.Additive);
            EditorSceneManager.SetActiveScene(sandbox);
            return sandbox;
        }

        /// <summary>
        /// Version-stable identity token for an open scene: the runtime scene
        /// handle widened to long. Valid for equality within one Editor
        /// session; pair with <see cref="FindOpenSceneByHandle"/>.
        /// </summary>
        public static long HandleOf(Scene scene) => scene.handle;

        /// <summary>Find an open scene by a token from <see cref="HandleOf"/>.</summary>
        public static Scene FindOpenSceneByHandle(long handle)
        {
            for (var i = 0; i < EditorSceneManager.sceneCount; i++)
            {
                var scene = EditorSceneManager.GetSceneAt(i);
                if (scene.handle == handle) return scene;
            }
            return default;
        }

        /// <summary>
        /// Restore the captured state and dispose the sandbox scene. Returns
        /// the restoration outcome; a failure never throws — it is reported so
        /// the caller can surface <c>restored:false</c> with a cause instead of
        /// pretending the Editor is clean.
        /// </summary>
        public static (bool Restored, string? Cause) RestoreCapturedState(
            SceneStateCapture capture,
            Scene sandboxScene)
        {
            if (capture == null) return (false, "capture-missing");
            string? cause = null;

            try
            {
                // Re-activate the previously active scene before closing the
                // sandbox so the Editor never observes a window without setup.
                var previousActive = FindCapturedActiveScene(capture);
                if (previousActive.IsValid() && sandboxScene.IsValid() && SceneManager.GetActiveScene().handle == sandboxScene.handle)
                    EditorSceneManager.SetActiveScene(previousActive);

                if (sandboxScene.IsValid() && sandboxScene.isLoaded)
                {
                    var closed = EditorSceneManager.CloseScene(sandboxScene, true);
                    if (!closed)
                        cause = "sandbox-scene-close-refused";
                }

                // Selection is restored only when it actually drifted, so a
                // sandbox that never touched the selection is a no-op here.
                if (!Selection.instanceIDs.SequenceEqual(capture.SelectionInstanceIds))
                    Selection.instanceIDs = (int[])capture.SelectionInstanceIds;

                // Setup drift beyond the sandbox scene (scripts closing/opening
                // user scenes) is restored from the capture.
                if (SetupDrifted(capture))
                {
                    var setup = capture.Setup.Select(entry => new SceneSetup
                    {
                        path = entry.Path,
                        isLoaded = entry.IsLoaded,
                        isActive = entry.IsActive,
                    }).ToArray();
                    EditorSceneManager.RestoreSceneManagerSetup(setup);
                }
            }
            catch (Exception ex)
            {
                return (false, cause ?? ex.Message);
            }

            return (cause == null, cause);
        }

        static IEnumerable<Scene> OpenScenes()
        {
            for (var i = 0; i < EditorSceneManager.sceneCount; i++)
                yield return EditorSceneManager.GetSceneAt(i);
        }

        static Scene FindCapturedActiveScene(SceneStateCapture capture)
        {
            var active = capture.Setup.FirstOrDefault(entry => entry.IsActive);
            if (active == null) return default;
            foreach (var scene in OpenScenes())
            {
                if (scene.path == active.Path) return scene;
            }
            return default;
        }

        static bool SetupDrifted(SceneStateCapture capture)
        {
            var current = EditorSceneManager.GetSceneManagerSetup() ?? Array.Empty<SceneSetup>();
            if (current.Length != capture.Setup.Length) return true;
            for (var i = 0; i < current.Length; i++)
            {
                if (current[i].path != capture.Setup[i].Path
                    || current[i].isLoaded != capture.Setup[i].IsLoaded
                    || current[i].isActive != capture.Setup[i].IsActive)
                {
                    return true;
                }
            }
            return false;
        }

        // ── persistence for sandbox contexts that must survive reloads ──────
        // (used by tests-run, whose completion callback may run in a new domain)

        public static string Persist(SceneStateCapture capture)
        {
            return JsonSerializer.Serialize(new PersistedSandboxState
            {
                Setup = capture.Setup,
                SelectionInstanceIds = capture.SelectionInstanceIds,
                DirtySceneHandles = capture.DirtySceneHandles,
            });
        }

        public static SceneStateCapture? Load(string json)
        {
            try
            {
                var state = JsonSerializer.Deserialize<PersistedSandboxState>(json);
                if (state == null) return null;
                return new SceneStateCapture
                {
                    Setup = state.Setup ?? Array.Empty<SceneStateCapture.SceneSetupEntry>(),
                    SelectionInstanceIds = state.SelectionInstanceIds ?? Array.Empty<int>(),
                    DirtySceneHandles = state.DirtySceneHandles ?? Array.Empty<int>(),
                };
            }
            catch
            {
                return null;
            }
        }

        sealed class PersistedSandboxState
        {
            public SceneStateCapture.SceneSetupEntry[] Setup { get; set; }
                = Array.Empty<SceneStateCapture.SceneSetupEntry>();
            public int[] SelectionInstanceIds { get; set; } = Array.Empty<int>();
            public int[] DirtySceneHandles { get; set; } = Array.Empty<int>();
        }
    }

    /// <summary>Outcome of consuming a persisted tests-run scene context.</summary>
    internal sealed class RunSceneOutcome
    {
        /// <summary>False when nothing was persisted for the operation.</summary>
        public bool HadContext;

        /// <summary>Null when the run was not sandboxed.</summary>
        public bool? SandboxRestored;

        public string? SandboxRestoreCause;

        public SceneMutationReport Mutation;
    }

    /// <summary>
    /// SessionState-backed sandbox + mutation context for tests-run. The
    /// capture must outlive the domain reloads a test run can trigger, so both
    /// the restorable state and the mutation baseline (in path form, which
    /// survives reloads) are persisted under the owning operation id and
    /// consumed exactly once by the run's completion (or reclaimed when the
    /// operation terminates abnormally).
    /// </summary>
    internal static class TestRunSceneSandbox
    {
        const string KeyPrefix = "UnityCopilot.TestSandbox.";

        static string Key(string operationId) => KeyPrefix + operationId;

        public static void PersistForOperation(
            string operationId,
            SceneStateCapture? capture,
            SceneMutationBaseline baseline)
        {
            if (string.IsNullOrEmpty(operationId) || baseline == null) return;
            var state = new PersistedRunContext
            {
                // A null capture JSON marks a non-sandboxed run that still
                // owes a mutation report.
                CaptureJson = capture == null ? null : EditorSceneSandbox.Persist(capture),
                BaselineActivePath = SceneManager.GetActiveScene().path,
                BaselineSelectionInstanceIds = baseline.SelectionInstanceIds,
                BaselineDirtyPaths = OpenScenePaths(baseline.DirtySceneHandles),
            };
            SessionState.SetString(Key(operationId), JsonSerializer.Serialize(state));
        }

        public static bool HasContext(string? operationId)
            => !string.IsNullOrEmpty(operationId)
            && SessionState.GetString(Key(operationId!), string.Empty).Length > 0;

        /// <summary>
        /// Restore the sandbox captured for an operation, clear the persisted
        /// context, and report the observed mutation. Safe to call when no
        /// context was persisted (returns a clean outcome).
        /// </summary>
        public static RunSceneOutcome RestoreForOperation(string? operationId)
        {
            var storedJson = string.IsNullOrEmpty(operationId)
                ? string.Empty
                : SessionState.GetString(Key(operationId!), string.Empty);
            if (storedJson.Length == 0)
            {
                return new RunSceneOutcome
                {
                    HadContext = false,
                    SandboxRestored = null,
                    SandboxRestoreCause = null,
                    Mutation = SceneMutationReport.None,
                };
            }

            var json = storedJson;
            SessionState.EraseString(Key(operationId!));

            PersistedRunContext? context;
            try
            {
                context = JsonSerializer.Deserialize<PersistedRunContext>(json);
            }
            catch
            {
                context = null;
            }
            if (context == null)
            {
                // A persisted context existed but cannot be decoded; report a
                // failed restoration so the result never pretends a clean run.
                return new RunSceneOutcome
                {
                    HadContext = true,
                    SandboxRestored = false,
                    SandboxRestoreCause = "capture-unreadable-after-reload",
                    Mutation = SceneMutationReport.None,
                };
            }

            var outcome = new RunSceneOutcome { HadContext = true };
            if (context.CaptureJson != null)
            {
                var capture = EditorSceneSandbox.Load(context.CaptureJson);
                if (capture == null)
                {
                    outcome.SandboxRestored = false;
                    outcome.SandboxRestoreCause = "capture-unreadable-after-reload";
                }
                else
                {
                    // The sandbox scene itself did not survive an Editor
                    // restart; any still-open untitled scene that is not part
                    // of the captured setup is the sandbox (domain-reload
                    // case) and is disposed on restore.
                    var sandbox = default(Scene);
                    var capturedPaths = new HashSet<string>(
                        capture.Setup.Select(entry => entry.Path),
                        StringComparer.Ordinal);
                    foreach (var scene in Enumerable.Range(0, EditorSceneManager.sceneCount)
                        .Select(EditorSceneManager.GetSceneAt))
                    {
                        if (scene.path.Length == 0 && !capturedPaths.Contains(string.Empty))
                        {
                            sandbox = scene;
                            break;
                        }
                    }
                    var restore = EditorSceneSandbox.RestoreCapturedState(capture, sandbox);
                    outcome.SandboxRestored = restore.Restored;
                    outcome.SandboxRestoreCause = restore.Cause;
                }
            }

            outcome.Mutation = DiffFromPersisted(context);
            return outcome;
        }

        static SceneMutationReport DiffFromPersisted(PersistedRunContext context)
        {
            var baselineDirty = new HashSet<string>(context.BaselineDirtyPaths, StringComparer.Ordinal);
            var mutatedScenes = new List<string>();
            foreach (var scene in Enumerable.Range(0, EditorSceneManager.sceneCount)
                .Select(EditorSceneManager.GetSceneAt))
            {
                if (scene.isDirty && !baselineDirty.Contains(scene.path))
                {
                    mutatedScenes.Add(scene.path.Length > 0 ? scene.path : "Untitled");
                }
            }
            var activeChanged = SceneManager.GetActiveScene().path != context.BaselineActivePath;
            var selectionChanged = !Selection.instanceIDs.SequenceEqual(
                context.BaselineSelectionInstanceIds);
            var mutated = mutatedScenes.Count > 0 || activeChanged || selectionChanged;
            return mutated
                ? new SceneMutationReport(true, mutatedScenes.ToArray())
                : SceneMutationReport.None;
        }

        static string[] OpenScenePaths(int[] handles)
        {
            var handleSet = new HashSet<int>(handles);
            return Enumerable.Range(0, EditorSceneManager.sceneCount)
                .Select(EditorSceneManager.GetSceneAt)
                .Where(scene => handleSet.Contains(scene.handle))
                .Select(scene => scene.path)
                .ToArray();
        }

        sealed class PersistedRunContext
        {
            public string? CaptureJson { get; set; }
            public string BaselineActivePath { get; set; } = string.Empty;
            public int[] BaselineSelectionInstanceIds { get; set; } = Array.Empty<int>();
            public string[] BaselineDirtyPaths { get; set; } = Array.Empty<string>();
        }
    }
}
#endif
