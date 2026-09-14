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
using System.ComponentModel;
using com.IvanMurzak.McpPlugin;
using com.IvanMurzak.ReflectorNet.Utils;
using com.AtelierAI.Unity.Copilot.Editor.Utils;
using com.AtelierAI.Unity.Copilot.Runtime.Extensions;
using AIGD;
using UnityEditor;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    public partial class Tool_Camera
    {
        public const string CameraCmVcamPriorityToolId = "camera-cm-vcam-priority";

        [McpPluginTool
        (
            CameraCmVcamPriorityToolId,
            Title = "Camera / Cinemachine / Set VCam Priority",
            DestructiveHint = true
        )]
        [McpPluginSkillDescription("Set the priority of a Cinemachine virtual-camera (CM 2.x or CM 3.x). " +
            "Persists into prefab/scene via `SerializedProperty` — do not bypass with `vcam.Priority = X`. " +
            "Returns Ok=false when the Cinemachine package is not installed.")]
        [McpPluginSkillBody("Sets `Priority` on a Cinemachine virtual-camera component.\n\n" +
            "## CM-version compatibility\n\n" +
            "- CM 2.x — `CinemachineVirtualCamera.Priority` is a public `int` field.\n" +
            "- CM 3.x — `CinemachineCamera.Priority` is a `PrioritySettings` struct with " +
            "`Enabled` and `Value`. This tool writes through the nested `Value` and flips `Enabled=true`.\n\n" +
            "Both code paths go through `SerializedObject` / `SerializedProperty` so the change is persisted " +
            "to the scene or prefab asset.\n\n" +
            "## Inputs\n\n" +
            "- `vcamRef` — host GameObject of the virtual-camera component.\n" +
            "- `priority` — integer priority used by `CinemachineBrain` to select the live camera.")]
        [Description("Set a Cinemachine virtual-camera's priority (persisted via SerializedProperty). " +
            "Returns Ok=false when Cinemachine is not installed.")]
        public CameraConfigureResult SetCmVcamPriority
        (
            [Description("Target GameObject hosting the Cinemachine virtual-camera component.")]
            GameObjectRef vcamRef,
            [Description("New priority value. Higher = more likely to become the live camera.")]
            int priority
        )
        {
            if (vcamRef == null)
                return new CameraConfigureResult { Ok = false, Error = Error.GameObjectRefRequired() };

            if (!vcamRef.IsValid(out var refErr))
                return new CameraConfigureResult { Ok = false, Error = refErr };

            if (!IsCinemachineAvailable())
                return new CameraConfigureResult { Ok = false, Error = Error.CinemachineNotInstalled() };

            return MainThread.Instance.Run(() =>
            {
                var go = vcamRef.FindGameObject(out var findErr);
                if (findErr != null || go == null)
                    return new CameraConfigureResult
                    {
                        Ok = false,
                        Error = findErr ?? "GameObject not found."
                    };

                var vcam = FindCinemachineVcam(go);
                if (vcam == null)
                    return new CameraConfigureResult
                    {
                        Ok = false,
                        GameObjectPath = GetHierarchyPath(go),
                        Error = Error.CinemachineVcamMissing(go.name)
                    };

                bool wrote;
                using (var so = new SerializedObject(vcam))
                {
                    wrote = WritePriority(so, priority);
                    if (wrote)
                        so.ApplyModifiedProperties();
                }

                if (!wrote)
                    return new CameraConfigureResult
                    {
                        Ok = false,
                        GameObjectPath = GetHierarchyPath(go),
                        Snapshot = BuildSnapshot(go),
                        Error = $"Could not locate a writable 'Priority' SerializedProperty on '{vcam.GetType().FullName}'."
                    };

                EditorUtility.SetDirty(vcam);
                EditorUtility.SetDirty(go);
                EditorUtils.RepaintAllEditorWindows();

                return new CameraConfigureResult
                {
                    Ok = true,
                    GameObjectPath = GetHierarchyPath(go),
                    Snapshot = BuildSnapshot(go)
                };
            });
        }
    }
}
