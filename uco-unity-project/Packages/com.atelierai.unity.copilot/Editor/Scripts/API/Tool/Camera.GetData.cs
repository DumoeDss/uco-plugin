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
using com.AtelierAI.Uco.Framework;
using com.IvanMurzak.ReflectorNet.Utils;
using com.AtelierAI.Unity.Copilot.Runtime.Extensions;
using AIGD;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    public partial class Tool_Camera
    {
        public const string CameraGetDataToolId = "camera-get-data";

        [UcoTool
        (
            CameraGetDataToolId,
            Title = "Camera / Get Data",
            ReadOnlyHint = true,
            IdempotentHint = true
        )]
        [UcoSkillDescription("Read the current configuration of a `UnityEngine.Camera` " +
            "(FOV, clear flags, culling mask, clip planes, ortho mode, depth) plus a Cinemachine " +
            "virtual-camera priority when one is present on the same GameObject. " +
            "Pair with '" + CameraConfigureToolId + "' to write back.")]
        [UcoSkillBody("Returns a `CameraConfigureResult` whose `Snapshot` describes the camera " +
            "and (when Cinemachine is installed) the priority + component type of any virtual-camera on " +
            "the same GameObject.\n\n" +
            "## Inputs\n\n" +
            "- `cameraRef` — host GameObject of the Camera component. Required.\n\n" +
            "## Behavior\n\n" +
            "Runs on the Unity main thread. Never mutates state. Returns Ok=false with an error " +
            "string when the GameObject cannot be resolved or has no Camera component.")]
        [Description("Read a Camera's configuration plus optional Cinemachine virtual-camera priority. " +
            "Pair with '" + CameraConfigureToolId + "' to write back.")]
        public CameraConfigureResult GetData
        (
            [Description("Target Camera GameObject. Use 'gameobject-find' to locate it.")]
            GameObjectRef cameraRef
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
