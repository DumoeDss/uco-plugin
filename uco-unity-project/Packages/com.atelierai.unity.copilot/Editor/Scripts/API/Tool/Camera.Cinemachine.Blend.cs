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
using com.AtelierAI.Uco.Framework;
using com.IvanMurzak.ReflectorNet.Utils;
using com.AtelierAI.Unity.Copilot.Editor.Utils;
using UnityEditor;
using UnityEngine;
using Component = UnityEngine.Component;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    public partial class Tool_Camera
    {
        public const string CameraCmSetDefaultBlendToolId = "camera-cm-set-default-blend";

        [UcoTool
        (
            CameraCmSetDefaultBlendToolId,
            Title = "Camera / Cinemachine / Set Default Blend",
            DestructiveHint = true
        )]
        [UcoSkillDescription("Configure the `DefaultBlend` field on a `CinemachineBrain` (the global default " +
            "blend between virtual cameras). Locates the Brain automatically — pass null/empty arguments to keep a " +
            "field unchanged. Returns Ok=false when the Cinemachine package is not installed or no Brain exists.")]
        [UcoSkillBody("Writes the Brain's default blend `Style` and/or `Time` via `SerializedProperty`. " +
            "Both fields are optional; only those supplied are modified.\n\n" +
            "## Inputs\n\n" +
            "- `style` — blend style enum name. Common values: `Cut`, `EaseInOut`, `EaseIn`, `EaseOut`, `Linear`, " +
            "`HardIn`, `HardOut`. Matched case-insensitively against the live enum on the property.\n" +
            "- `duration` — blend duration in seconds. Negative values are ignored.\n\n" +
            "## Behavior\n\n" +
            "Locates the first `CinemachineBrain` via reflection + `Object.FindObjectsByType`. Persists changes " +
            "through `SerializedProperty` so they survive scene save.")]
        [Description("Set the default blend (style + duration) on the active CinemachineBrain. " +
            "Returns Ok=false when Cinemachine is not installed or no Brain is present.")]
        public CameraConfigureResult SetDefaultBlend
        (
            [Description("Blend style name (e.g. Cut, EaseInOut, Linear, HardIn, HardOut). Ignored when null/empty.")]
            string? style = null,
            [Description("Blend duration in seconds. Negative values are ignored.")]
            float? duration = null
        )
        {
            if (!IsCinemachineAvailable())
                return new CameraConfigureResult { Ok = false, Error = Error.CinemachineNotInstalled() };

            return MainThread.Instance.Run(() =>
            {
                var brainType = GetCinemachineBrainType();
                if (brainType == null)
                    return new CameraConfigureResult { Ok = false, Error = Error.CinemachineNotInstalled() };

                var brains = UnityEngine.Object.FindObjectsByType(
                    brainType, FindObjectsInactive.Include, FindObjectsSortMode.None);
                if (brains == null || brains.Length == 0)
                    return new CameraConfigureResult
                    {
                        Ok = false,
                        Error = "No CinemachineBrain found in the active scenes."
                    };

                if (!(brains[0] is Component brain))
                    return new CameraConfigureResult
                    {
                        Ok = false,
                        Error = "Resolved CinemachineBrain is not a UnityEngine.Component."
                    };

                using var so = new SerializedObject(brain);
                var defaultBlend = so.FindProperty("DefaultBlend") ?? so.FindProperty("m_DefaultBlend");
                if (defaultBlend == null)
                    return new CameraConfigureResult
                    {
                        Ok = false,
                        GameObjectPath = GetHierarchyPath(brain.gameObject),
                        Error = "Could not find 'DefaultBlend' SerializedProperty on CinemachineBrain."
                    };

                if (!string.IsNullOrEmpty(style))
                {
                    var styleProp = defaultBlend.FindPropertyRelative("Style")
                                 ?? defaultBlend.FindPropertyRelative("m_Style");
                    if (styleProp != null && styleProp.propertyType == SerializedPropertyType.Enum)
                    {
                        var names = styleProp.enumNames;
                        var idx = Array.FindIndex(
                            names,
                            n => string.Equals(n, style, StringComparison.OrdinalIgnoreCase));
                        if (idx < 0)
                            return new CameraConfigureResult
                            {
                                Ok = false,
                                GameObjectPath = GetHierarchyPath(brain.gameObject),
                                Error = $"Unknown blend style '{style}'. Expected one of: {string.Join(", ", names)}."
                            };
                        styleProp.enumValueIndex = idx;
                    }
                }

                if (duration.HasValue && duration.Value >= 0f)
                {
                    var timeProp = defaultBlend.FindPropertyRelative("Time")
                                ?? defaultBlend.FindPropertyRelative("m_Time");
                    if (timeProp != null && timeProp.propertyType == SerializedPropertyType.Float)
                        timeProp.floatValue = duration.Value;
                }

                so.ApplyModifiedProperties();
                EditorUtility.SetDirty(brain);
                EditorUtility.SetDirty(brain.gameObject);
                EditorUtils.RepaintAllEditorWindows();

                return new CameraConfigureResult
                {
                    Ok = true,
                    GameObjectPath = GetHierarchyPath(brain.gameObject),
                    Snapshot = BuildSnapshot(brain.gameObject)
                };
            });
        }
    }
}
