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
using UnityEngine;
using Component = UnityEngine.Component;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    [UcoToolType]
    public partial class Tool_Camera
    {
        // ---------------------------------------------------------------
        // Error / message helpers
        // ---------------------------------------------------------------
        public static class Error
        {
            public static string CinemachineNotInstalled() =>
                "Cinemachine package not installed.";

            public static string CameraComponentMissing(string goName) =>
                $"No 'UnityEngine.Camera' component found on GameObject '{goName}'.";

            public static string CinemachineVcamMissing(string goName) =>
                $"No Cinemachine virtual camera component found on GameObject '{goName}'.";

            public static string UnknownClearFlags(string value) =>
                $"Unknown clearFlags value '{value}'. Expected one of: Skybox, SolidColor, Depth, Nothing.";

            public static string GameObjectRefRequired() =>
                "GameObject reference is required.";
        }

        // ---------------------------------------------------------------
        // DTOs
        // ---------------------------------------------------------------

        /// <summary>
        /// Result returned by Camera tool calls that mutate a Camera or virtual camera.
        /// </summary>
        public class CameraConfigureResult
        {
            [Description("True when the operation completed without an error.")]
            public bool Ok { get; set; }

            [Description("Hierarchy path of the target GameObject, when resolved.")]
            public string? GameObjectPath { get; set; }

            [Description("Error message when Ok is false. Null on success.")]
            public string? Error { get; set; }

            [Description("Snapshot of the camera state after the operation. " +
                "Null when no GameObject was resolved.")]
            public CameraDataSnapshot? Snapshot { get; set; }
        }

        /// <summary>
        /// Read-only snapshot of an <see cref="UnityEngine.Camera"/> plus optional
        /// Cinemachine virtual-camera info.
        /// </summary>
        public class CameraDataSnapshot
        {
            [Description("Hierarchy path of the camera GameObject.")]
            public string Path { get; set; } = "";

            [Description("Camera.fieldOfView (degrees).")]
            public float FieldOfView { get; set; }

            [Description("Camera.clearFlags as enum name (Skybox / SolidColor / Depth / Nothing).")]
            public string ClearFlags { get; set; } = "";

            [Description("Camera.backgroundColor as a Vector4 (r,g,b,a in [0..1]).")]
            public Vector4 BackgroundColor { get; set; }

            [Description("Camera.cullingMask bitmask.")]
            public int CullingMask { get; set; }

            [Description("Camera.nearClipPlane.")]
            public float NearClipPlane { get; set; }

            [Description("Camera.farClipPlane.")]
            public float FarClipPlane { get; set; }

            [Description("Camera.orthographic flag.")]
            public bool Orthographic { get; set; }

            [Description("Camera.orthographicSize (only meaningful when Orthographic is true).")]
            public float OrthographicSize { get; set; }

            [Description("Camera.depth — render-order value.")]
            public float Depth { get; set; }

            [Description("Cinemachine virtual-camera priority. Null when Cinemachine is not installed " +
                "or no virtual-camera component is present on the GameObject.")]
            public int? CmPriority { get; set; }

            [Description("Cinemachine virtual-camera component type short name " +
                "('CinemachineVirtualCamera' / 'CinemachineCamera'), or null when not present.")]
            public string? CmType { get; set; }
        }

        // ---------------------------------------------------------------
        // Cinemachine package detection (reflection-only — no NuGet dep)
        // ---------------------------------------------------------------

        /// <summary>
        /// Detects whether the Cinemachine package is available in the current project.
        /// Probes both CM 2.x and CM 3.x assembly-qualified names.
        /// </summary>
        internal static bool IsCinemachineAvailable() => GetCinemachineVcamType() != null;

        /// <summary>
        /// Returns the resolved Cinemachine virtual-camera <see cref="Type"/>, or null when the
        /// package is not installed. CM 3.x's <c>Unity.Cinemachine.CinemachineCamera</c> is
        /// preferred when both are present.
        /// </summary>
        internal static Type? GetCinemachineVcamType()
        {
            try
            {
                // CM 3.x first — modern API surface.
                var t3 = Type.GetType("Unity.Cinemachine.CinemachineCamera, Unity.Cinemachine", false);
                if (t3 != null) return t3;

                // CM 2.x fallback.
                var t2 = Type.GetType("Cinemachine.CinemachineVirtualCamera, Cinemachine", false);
                if (t2 != null) return t2;
            }
            catch
            {
                // Reflection probing must never throw — treat as "not installed".
            }
            return null;
        }

        internal static Type? GetCinemachineBrainType()
        {
            try
            {
                return Type.GetType("Unity.Cinemachine.CinemachineBrain, Unity.Cinemachine", false)
                    ?? Type.GetType("Cinemachine.CinemachineBrain, Cinemachine", false);
            }
            catch { return null; }
        }

        /// <summary>
        /// Locate the Cinemachine virtual-camera <see cref="Component"/> on a GameObject without
        /// taking a hard dependency on the Cinemachine assembly. Returns null when CM is not
        /// installed or when the GameObject does not host such a component.
        /// </summary>
        internal static UnityEngine.Component? FindCinemachineVcam(GameObject go)
        {
            if (go == null) return null;
            var vcamType = GetCinemachineVcamType();
            if (vcamType == null) return null;
            return go.GetComponent(vcamType);
        }

        // ---------------------------------------------------------------
        // Snapshot builder — read-only inspection of a Camera + optional CM vcam.
        // ---------------------------------------------------------------

        /// <summary>
        /// Build a <see cref="CameraDataSnapshot"/> for the given GameObject. The GameObject must
        /// host a <see cref="UnityEngine.Camera"/> component; CM data is added only when a CM
        /// virtual-camera is also present on the same GameObject.
        /// Must be called on the Unity main thread.
        /// </summary>
        internal static CameraDataSnapshot? BuildSnapshot(GameObject go)
        {
            if (go == null) return null;
            var cam = go.GetComponent<UnityEngine.Camera>();
            if (cam == null) return null;

            var snap = new CameraDataSnapshot
            {
                Path = GetHierarchyPath(go),
                FieldOfView = cam.fieldOfView,
                ClearFlags = cam.clearFlags.ToString(),
                BackgroundColor = new Vector4(
                    cam.backgroundColor.r,
                    cam.backgroundColor.g,
                    cam.backgroundColor.b,
                    cam.backgroundColor.a),
                CullingMask = cam.cullingMask,
                NearClipPlane = cam.nearClipPlane,
                FarClipPlane = cam.farClipPlane,
                Orthographic = cam.orthographic,
                OrthographicSize = cam.orthographicSize,
                Depth = cam.depth
            };

            var vcam = FindCinemachineVcam(go);
            if (vcam != null)
            {
                snap.CmType = vcam.GetType().Name;
                snap.CmPriority = ReadCinemachinePriority(vcam);
            }

            return snap;
        }

        /// <summary>
        /// Reads the Cinemachine virtual-camera Priority via SerializedObject so it works for
        /// both CM 2.x (public int) and CM 3.x (PrioritySettings struct).
        /// Returns null when the property cannot be resolved.
        /// </summary>
        internal static int? ReadCinemachinePriority(Component vcam)
        {
            if (vcam == null) return null;
            try
            {
                using var so = new UnityEditor.SerializedObject(vcam);
                var prop = so.FindProperty("Priority")
                        ?? so.FindProperty("m_Priority")
                        ?? so.FindProperty("priority");
                if (prop == null) return null;

                if (prop.propertyType == UnityEditor.SerializedPropertyType.Integer)
                    return prop.intValue;

                // CM 3.x PrioritySettings struct → has nested 'Value' (or 'm_Value').
                var valueProp = prop.FindPropertyRelative("Value")
                             ?? prop.FindPropertyRelative("m_Value");
                return valueProp?.intValue;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Compute the slash-separated hierarchy path of a GameObject (e.g. "Root/Child/Camera").
        /// </summary>
        internal static string GetHierarchyPath(GameObject go)
        {
            if (go == null) return "";
            var t = go.transform;
            var path = t.name;
            while (t.parent != null)
            {
                t = t.parent;
                path = t.name + "/" + path;
            }
            return path;
        }
    }
}

