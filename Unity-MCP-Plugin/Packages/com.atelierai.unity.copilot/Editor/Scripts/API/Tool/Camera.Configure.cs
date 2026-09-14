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
using com.AtelierAI.Unity.Copilot.Runtime.Extensions;
using AIGD;
using UnityEditor;
using UnityEngine;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    public partial class Tool_Camera
    {
        public const string CameraConfigureToolId = "camera-configure";

        [UcoTool
        (
            CameraConfigureToolId,
            Title = "Camera / Configure",
            DestructiveHint = true
        )]
        [UcoSkillDescription("Configure properties on a `UnityEngine.Camera` component (FOV, clear flags, " +
            "culling mask, clip planes, background color, orthographic mode/size, render-order depth). " +
            "Pass only the fields you want to change — null fields are left untouched. " +
            "Use '" + CameraGetDataToolId + "' to inspect the current values first.")]
        [UcoSkillBody("Configure a Unity Camera. Only the supplied (non-null) fields are written; everything else is left as-is.\n\n" +
            "## Inputs\n\n" +
            "- `cameraRef` — host GameObject of the Camera component. Required.\n" +
            "- `fieldOfView` — perspective FOV in degrees.\n" +
            "- `clearFlags` — one of `Skybox`, `SolidColor`, `Depth`, `Nothing` (case-insensitive).\n" +
            "- `backgroundColor` — RGBA in [0..1]; combined with `clearFlags=SolidColor`.\n" +
            "- `cullingMask` — layer bitmask.\n" +
            "- `nearClipPlane` / `farClipPlane` — view-frustum clip planes.\n" +
            "- `orthographic` / `orthographicSize` — orthographic toggle + half-height.\n" +
            "- `depth` — render-order depth (also used as the camera 'priority' in the absence of Cinemachine).\n\n" +
            "## Behavior\n\n" +
            "Runs on the Unity main thread. Mutates the Camera via direct property writes, marks it dirty, " +
            "and returns a `CameraConfigureResult` with `Snapshot` reflecting the post-write state.")]
        [Description("Configure a UnityEngine.Camera's most-common fields. " +
            "Only provided (non-null) parameters are written. " +
            "Use '" + CameraGetDataToolId + "' to inspect current values first.")]
        public CameraConfigureResult Configure
        (
            [Description("Target Camera GameObject. Use 'gameobject-find' to locate it.")]
            GameObjectRef cameraRef,
            [Description("Field of View in degrees (perspective). Ignored when null.")]
            float? fieldOfView = null,
            [Description("Clear flags: Skybox | SolidColor | Depth | Nothing. Ignored when null.")]
            string? clearFlags = null,
            [Description("Background color (RGBA in [0..1]). Used together with clearFlags=SolidColor. Ignored when null.")]
            Vector4? backgroundColor = null,
            [Description("Culling-mask bitmask. Ignored when null.")]
            int? cullingMask = null,
            [Description("Near clip plane. Ignored when null.")]
            float? nearClipPlane = null,
            [Description("Far clip plane. Ignored when null.")]
            float? farClipPlane = null,
            [Description("Orthographic mode. Ignored when null.")]
            bool? orthographic = null,
            [Description("Orthographic size (half-height in world units). Used when orthographic=true. Ignored when null.")]
            float? orthographicSize = null,
            [Description("Camera depth (render order / fallback 'priority'). Ignored when null.")]
            float? depth = null
        )
        {
            if (cameraRef == null)
                return new CameraConfigureResult { Ok = false, Error = Error.GameObjectRefRequired() };

            if (!cameraRef.IsValid(out var refErr))
                return new CameraConfigureResult { Ok = false, Error = refErr };

            return MainThread.Instance.Run(() =>
            {
                var go = cameraRef.FindGameObject(out var findErr);
                if (findErr != null || go == null)
                    return new CameraConfigureResult
                    {
                        Ok = false,
                        Error = findErr ?? "GameObject not found."
                    };

                var cam = go.GetComponent<UnityEngine.Camera>();
                if (cam == null)
                    return new CameraConfigureResult
                    {
                        Ok = false,
                        GameObjectPath = GetHierarchyPath(go),
                        Error = Error.CameraComponentMissing(go.name)
                    };

                Undo.RecordObject(cam, "Configure Camera");

                if (fieldOfView.HasValue)
                    cam.fieldOfView = fieldOfView.Value;

                if (!string.IsNullOrEmpty(clearFlags))
                {
                    if (!Enum.TryParse<CameraClearFlags>(clearFlags, ignoreCase: true, out var flags))
                        return new CameraConfigureResult
                        {
                            Ok = false,
                            GameObjectPath = GetHierarchyPath(go),
                            Error = Error.UnknownClearFlags(clearFlags!)
                        };
                    cam.clearFlags = flags;
                }

                if (backgroundColor.HasValue)
                {
                    var bg = backgroundColor.Value;
                    cam.backgroundColor = new Color(bg.x, bg.y, bg.z, bg.w);
                }

                if (cullingMask.HasValue)
                    cam.cullingMask = cullingMask.Value;

                if (nearClipPlane.HasValue)
                    cam.nearClipPlane = nearClipPlane.Value;

                if (farClipPlane.HasValue)
                    cam.farClipPlane = farClipPlane.Value;

                if (orthographic.HasValue)
                    cam.orthographic = orthographic.Value;

                if (orthographicSize.HasValue)
                    cam.orthographicSize = orthographicSize.Value;

                if (depth.HasValue)
                    cam.depth = depth.Value;

                EditorUtility.SetDirty(cam);
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
