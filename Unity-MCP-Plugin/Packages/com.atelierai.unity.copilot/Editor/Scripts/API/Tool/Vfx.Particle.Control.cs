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
using com.AtelierAI.Unity.Copilot.Runtime.Extensions;
using AIGD;
using UnityEditor;
using UnityEngine;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    public partial class Tool_Vfx
    {
        public const string VfxParticleControlToolId = "vfx-particle-control";

        [McpPluginTool
        (
            VfxParticleControlToolId,
            Title = "VFX / Particle / Control",
            DestructiveHint = true
        )]
        [McpPluginSkillDescription("Control playback of a `UnityEngine.ParticleSystem` — play / pause / stop / " +
            "clear / emit. The 'emit' action injects `emitCount` particles immediately via `ParticleSystem.Emit(int)`.")]
        [McpPluginSkillBody("Control ParticleSystem playback. Operates on the supplied GameObject's ParticleSystem " +
            "component plus its children (matching Unity's default 'withChildren=true' behavior).\n\n" +
            "## Actions\n\n" +
            "- `play` — `ParticleSystem.Play(withChildren: true)`.\n" +
            "- `pause` — `ParticleSystem.Pause(withChildren: true)`.\n" +
            "- `stop` — `ParticleSystem.Stop(withChildren: true, StopBehavior.StopEmitting)`.\n" +
            "- `clear` — `ParticleSystem.Clear(withChildren: true)`.\n" +
            "- `emit` — `ParticleSystem.Emit(emitCount)` on the root system only.\n\n" +
            "## Behavior\n\n" +
            "Runs on the Unity main thread. The returned `Snapshot` reflects the state immediately after the action.")]
        [Description("Control ParticleSystem playback (play / pause / stop / clear / emit). " +
            "Use 'emit' to inject a one-shot burst of N particles.")]
        public ParticleResult Control
        (
            [Description("Target GameObject hosting the ParticleSystem. Use 'gameobject-find' to locate it.")]
            GameObjectRef target,
            [Description("Action: 'play' | 'pause' | 'stop' | 'clear' | 'emit'.")]
            string action,
            [Description("Number of particles to inject when action='emit'. Ignored otherwise.")]
            int emitCount = 1
        )
        {
            if (target == null)
                return new ParticleResult { Ok = false, Error = Error.GameObjectRefRequired() };

            if (string.IsNullOrEmpty(action))
                return new ParticleResult { Ok = false, Error = Error.MissingAction() };

            if (!target.IsValid(out var refErr))
                return new ParticleResult { Ok = false, Error = refErr };

            return MainThread.Instance.Run(() =>
            {
                var go = target.FindGameObject(out var findErr);
                if (findErr != null || go == null)
                    return new ParticleResult
                    {
                        Ok = false,
                        Error = findErr ?? "GameObject not found."
                    };

                var ps = go.GetComponent<ParticleSystem>();
                if (ps == null)
                    return new ParticleResult
                    {
                        Ok = false,
                        GameObjectPath = GetHierarchyPath(go),
                        Error = Error.ParticleSystemMissing(go.name)
                    };

                Undo.RecordObject(ps, $"ParticleSystem.{action}");

                switch (action.Trim().ToLowerInvariant())
                {
                    case "play":
                        ps.Play(withChildren: true);
                        break;
                    case "pause":
                        ps.Pause(withChildren: true);
                        break;
                    case "stop":
                        ps.Stop(withChildren: true, ParticleSystemStopBehavior.StopEmitting);
                        break;
                    case "clear":
                        ps.Clear(withChildren: true);
                        break;
                    case "emit":
                        ps.Emit(emitCount < 0 ? 0 : emitCount);
                        break;
                    default:
                        return new ParticleResult
                        {
                            Ok = false,
                            GameObjectPath = GetHierarchyPath(go),
                            Error = Error.UnknownAction(action)
                        };
                }

                EditorUtility.SetDirty(ps);

                return new ParticleResult
                {
                    Ok = true,
                    GameObjectPath = GetHierarchyPath(go),
                    AppliedFields = action.Trim().ToLowerInvariant(),
                    Snapshot = BuildParticleSnapshot(go, ps)
                };
            });
        }
    }
}
