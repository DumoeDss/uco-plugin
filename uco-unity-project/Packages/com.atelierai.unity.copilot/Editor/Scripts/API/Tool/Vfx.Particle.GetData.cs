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
using UnityEngine;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    public partial class Tool_Vfx
    {
        public const string VfxParticleGetToolId = "vfx-particle-get";

        [UcoTool
        (
            VfxParticleGetToolId,
            Title = "VFX / Particle / Get",
            ReadOnlyHint = true,
            IdempotentHint = true
        )]
        [UcoSkillDescription("Read the current state of a `UnityEngine.ParticleSystem` — playback flags, " +
            "particle count, and a subset of the main / emission / shape / velocity-over-lifetime modules. " +
            "Pair with '" + VfxParticleConfigureToolId + "' to write back.")]
        [UcoSkillBody("Returns a `ParticleResult` whose `Snapshot` describes the ParticleSystem state.\n\n" +
            "## Inputs\n\n" +
            "- `target` — host GameObject of the ParticleSystem component. Required.\n\n" +
            "## Behavior\n\n" +
            "Runs on the Unity main thread. Never mutates state. Returns Ok=false with an error string when " +
            "the GameObject cannot be resolved or has no ParticleSystem component. The snapshot reads only the " +
            "constant value of each MinMaxCurve — curve/random-range modes are intentionally not surfaced.")]
        [Description("Read a ParticleSystem's main/emission/shape/velocity-over-lifetime modules. " +
            "Pair with '" + VfxParticleConfigureToolId + "' to write back.")]
        public ParticleResult GetParticle
        (
            [Description("Target GameObject hosting the ParticleSystem. Use 'gameobject-find' to locate it.")]
            GameObjectRef target
        )
        {
            if (target == null)
                return new ParticleResult { Ok = false, Error = Error.GameObjectRefRequired() };

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

                return new ParticleResult
                {
                    Ok = true,
                    GameObjectPath = GetHierarchyPath(go),
                    Snapshot = BuildParticleSnapshot(go, ps)
                };
            });
        }
    }
}
