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
using System.ComponentModel;
using com.IvanMurzak.McpPlugin;
using com.IvanMurzak.ReflectorNet.Utils;
using com.AtelierAI.Unity.Copilot.Editor.Utils;
using com.AtelierAI.Unity.Copilot.Runtime.Extensions;
using AIGD;
using UnityEditor;
using UnityEngine;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    public partial class Tool_Camera
    {
        public const string CameraCmVcamConfigureToolId = "camera-cm-vcam-configure";

        [McpPluginTool
        (
            CameraCmVcamConfigureToolId,
            Title = "Camera / Cinemachine / Configure VCam",
            DestructiveHint = true
        )]
        [McpPluginSkillDescription("Configure a Cinemachine virtual-camera (CM 2.x `CinemachineVirtualCamera` or " +
            "CM 3.x `CinemachineCamera`) — priority, lens FOV, and optional noise profile. " +
            "Cinemachine is detected via reflection at runtime: when the package is not installed, the call " +
            "returns Ok=false with `Error = \"" + nameof(Error.CinemachineNotInstalled) + "\"` instead of throwing.")]
        [McpPluginSkillBody("Configures a Cinemachine virtual-camera component on the target GameObject.\n\n" +
            "## Inputs\n\n" +
            "- `vcamRef` — host GameObject of the virtual-camera component.\n" +
            "- `priority` — Brain selection priority. Written via `SerializedProperty` to persist into prefab/scene.\n" +
            "- `fov` — lens field of view (degrees).\n" +
            "- `followLensIndex` — reserved for future expansion (lens-preset index). Currently ignored.\n" +
            "- `noiseProfileAssetPath` — asset path of a `NoiseSettings` ScriptableObject; when supplied, a " +
            "`CinemachineBasicMultiChannelPerlin` component is added/updated.\n" +
            "- `noiseAmplitudeGain` / `noiseFrequencyGain` — gains on the noise component.\n\n" +
            "## Behavior\n\n" +
            "Runs on the Unity main thread. All writes go through `SerializedProperty` so they persist into " +
            "the scene/prefab. When CM is not installed, returns Ok=false (does not throw).")]
        [Description("Configure a Cinemachine virtual-camera (priority, FOV, optional noise). " +
            "Returns Ok=false when the Cinemachine package is not installed.")]
        public CameraConfigureResult ConfigureCmVcam
        (
            [Description("Target GameObject hosting the Cinemachine virtual-camera component.")]
            GameObjectRef vcamRef,
            [Description("Cinemachine virtual-camera priority (Brain selection). Ignored when null.")]
            int? priority = null,
            [Description("Lens field of view (degrees). Ignored when null.")]
            float? fov = null,
            [Description("Reserved for future lens-preset index. Currently ignored.")]
            int? followLensIndex = null,
            [Description("Asset path of a NoiseSettings ScriptableObject. When supplied, a " +
                "CinemachineBasicMultiChannelPerlin component is added/updated. Ignored when null.")]
            string? noiseProfileAssetPath = null,
            [Description("Noise amplitude gain. Ignored when null.")]
            float? noiseAmplitudeGain = null,
            [Description("Noise frequency gain. Ignored when null.")]
            float? noiseFrequencyGain = null
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

                // --- Priority + FOV (Lens) on the vcam itself, persisted via SerializedProperty ---
                using (var so = new SerializedObject(vcam))
                {
                    if (priority.HasValue)
                        WritePriority(so, priority.Value);

                    if (fov.HasValue)
                        WriteLensFov(so, fov.Value);

                    if (so.hasModifiedProperties)
                        so.ApplyModifiedProperties();
                }

                EditorUtility.SetDirty(vcam);

                // --- Noise profile + gains (separate component) ---
                if (!string.IsNullOrEmpty(noiseProfileAssetPath)
                    || noiseAmplitudeGain.HasValue
                    || noiseFrequencyGain.HasValue)
                {
                    var noiseErr = ConfigureNoise(go, noiseProfileAssetPath, noiseAmplitudeGain, noiseFrequencyGain);
                    if (noiseErr != null)
                        return new CameraConfigureResult
                        {
                            Ok = false,
                            GameObjectPath = GetHierarchyPath(go),
                            Snapshot = BuildSnapshot(go),
                            Error = noiseErr
                        };
                }

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

        // ---------------------------------------------------------------
        // Helpers shared with Camera.Cinemachine.VcamPriority.cs
        // ---------------------------------------------------------------

        /// <summary>
        /// Write priority into a Cinemachine vcam's SerializedObject. Handles both CM 2.x
        /// (Priority is an int) and CM 3.x (PrioritySettings struct with nested Value).
        /// Returns true when a property was written.
        /// </summary>
        internal static bool WritePriority(SerializedObject so, int priority)
        {
            var prop = so.FindProperty("Priority")
                    ?? so.FindProperty("m_Priority")
                    ?? so.FindProperty("priority");
            if (prop == null) return false;

            if (prop.propertyType == SerializedPropertyType.Integer)
            {
                prop.intValue = priority;
                return true;
            }

            // CM 3.x — PrioritySettings { bool Enabled; int Value; }
            var enabledProp = prop.FindPropertyRelative("Enabled");
            var valueProp = prop.FindPropertyRelative("Value")
                         ?? prop.FindPropertyRelative("m_Value");

            if (valueProp == null) return false;

            if (enabledProp != null) enabledProp.boolValue = true;
            valueProp.intValue = priority;
            return true;
        }

        /// <summary>
        /// Write Lens.FieldOfView into a Cinemachine vcam's SerializedObject. Returns true on success.
        /// </summary>
        private static bool WriteLensFov(SerializedObject so, float fov)
        {
            var lensProp = so.FindProperty("Lens") ?? so.FindProperty("m_Lens");
            if (lensProp == null) return false;

            var fovProp = lensProp.FindPropertyRelative("FieldOfView")
                       ?? lensProp.FindPropertyRelative("m_FieldOfView");
            if (fovProp == null || fovProp.propertyType != SerializedPropertyType.Float) return false;

            fovProp.floatValue = fov;
            return true;
        }

        /// <summary>
        /// Add or update a CinemachineBasicMultiChannelPerlin component on the vcam GameObject.
        /// Returns an error string when the noise component type cannot be resolved or the
        /// noise-profile asset cannot be loaded; null on success.
        /// </summary>
        private static string? ConfigureNoise(
            GameObject go,
            string? noiseProfileAssetPath,
            float? amplitudeGain,
            float? frequencyGain)
        {
            var noiseType = Type.GetType("Unity.Cinemachine.CinemachineBasicMultiChannelPerlin, Unity.Cinemachine", false)
                         ?? Type.GetType("Cinemachine.CinemachineBasicMultiChannelPerlin, Cinemachine", false);
            if (noiseType == null)
                return "CinemachineBasicMultiChannelPerlin type not found in the loaded Cinemachine assembly.";

            var noiseComp = go.GetComponent(noiseType);
            if (noiseComp == null)
                noiseComp = Undo.AddComponent(go, noiseType);

            using var so = new SerializedObject(noiseComp);

            if (!string.IsNullOrEmpty(noiseProfileAssetPath))
            {
                var noiseAsset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(noiseProfileAssetPath);
                if (noiseAsset == null)
                    return $"Noise profile asset not found at '{noiseProfileAssetPath}'.";

                var profileProp = so.FindProperty("m_NoiseProfile")
                               ?? so.FindProperty("NoiseProfile")
                               ?? so.FindProperty("m_Definition");
                if (profileProp != null)
                    profileProp.objectReferenceValue = noiseAsset;
            }

            if (amplitudeGain.HasValue)
            {
                var ampProp = so.FindProperty("m_AmplitudeGain")
                           ?? so.FindProperty("AmplitudeGain");
                if (ampProp != null && ampProp.propertyType == SerializedPropertyType.Float)
                    ampProp.floatValue = amplitudeGain.Value;
            }

            if (frequencyGain.HasValue)
            {
                var freqProp = so.FindProperty("m_FrequencyGain")
                            ?? so.FindProperty("FrequencyGain");
                if (freqProp != null && freqProp.propertyType == SerializedPropertyType.Float)
                    freqProp.floatValue = frequencyGain.Value;
            }

            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(noiseComp);
            return null;
        }
    }
}
