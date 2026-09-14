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
using System.Collections.Generic;
using System.ComponentModel;
using com.IvanMurzak.McpPlugin;
using com.IvanMurzak.ReflectorNet.Utils;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AIGD
{
    /// <summary>Severity bucket for a <see cref="PhysicsValidationIssue"/>.</summary>
    public enum PhysicsValidationSeverity
    {
        Info = 0,
        Warning = 1,
        Error = 2
    }

    [Description("A single physics validation finding.")]
    public class PhysicsValidationIssue
    {
        [Description("Severity bucket: Info, Warning, or Error.")]
        public PhysicsValidationSeverity Severity { get; set; } = PhysicsValidationSeverity.Warning;
        [Description("Stable code identifying the validation rule (e.g. 'collider_no_rigidbody').")]
        public string Code { get; set; } = string.Empty;
        [Description("Scene path of the affected GameObject (or empty for project-level findings).")]
        public string Path { get; set; } = string.Empty;
        [Description("Human-readable message describing the issue.")]
        public string Message { get; set; } = string.Empty;
    }

    [Description("Result of a 'physics-validate' call.")]
    public class ValidationResult
    {
        [Description("True when the validation pass completed (regardless of whether any issues were found).")]
        public bool Ok { get; set; }
        [Description("Error message when Ok is false. Null on success.")]
        public string? Error { get; set; }
        [Description("List of findings. Each entry has a Severity, Code, Path and Message.")]
        public List<PhysicsValidationIssue> Issues { get; set; } = new List<PhysicsValidationIssue>();
        [Description("Total number of GameObjects scanned across all loaded scenes.")]
        public int ObjectsScanned { get; set; }
        [Description("Histogram of issue counts by Code.")]
        public Dictionary<string, int> Summary { get; set; } = new Dictionary<string, int>();
    }
}

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    using AIGD;

    public partial class Tool_Physics
    {
        public const string PhysicsValidateToolId = "physics-validate";

        // Issue codes — kept here as constants so callers can match deterministically.
        private const string Code_ColliderNoRigidbody = "collider_no_rigidbody";
        private const string Code_NonConvexMeshOnDynamic = "mesh_collider_non_convex_on_dynamic";
        private const string Code_RigidbodyMassZero = "rigidbody_mass_zero";
        private const string Code_Rigidbody2DMassZero = "rigidbody2d_mass_zero";
        private const string Code_NonUniformScale = "non_uniform_scale_with_collider";
        private const string Code_Mixed2D3D = "mixed_2d_3d_physics";
        private const string Code_SelfLayerCollisionDisabled = "self_layer_collision_disabled";
        private const string Code_MissingPhysicsMaterial = "missing_physics_material";

        [McpPluginTool
        (
            PhysicsValidateToolId,
            Title = "Physics / Validate",
            ReadOnlyHint = true,
            IdempotentHint = true
        )]
        [McpPluginSkillDescription("Scan all loaded scenes for common physics misconfigurations: " +
            "colliders without rigidbodies, non-convex MeshColliders on dynamic bodies, zero-mass rigidbodies, " +
            "non-uniform scale on collider hosts, mixed 2D/3D physics on the same GameObject, layers configured " +
            "to not collide with themselves, and colliders without a physics material.")]
        [McpPluginSkillBody("Walks the root GameObjects of every loaded `UnityEngine.SceneManagement.Scene` and " +
            "applies a fixed set of checks. Returns a `ValidationResult` listing each finding with a stable " +
            "`Code` (so test harnesses can match on it) plus a `Summary` histogram.\n\n" +
            "## Inputs\n\n" +
            "- `includeInfo` (default `false`) — when true, also emit `Info`-severity findings (e.g. collider " +
            "without a physics material). When false, only `Warning` / `Error` entries are returned.\n\n" +
            "## Issue codes\n\n" +
            "- `collider_no_rigidbody` — collider on a non-static GameObject with no Rigidbody on it or any parent.\n" +
            "- `mesh_collider_non_convex_on_dynamic` — MeshCollider without `convex` on a non-kinematic Rigidbody.\n" +
            "- `rigidbody_mass_zero` — Rigidbody with mass <= 0.\n" +
            "- `rigidbody2d_mass_zero` — Rigidbody2D with mass <= 0.\n" +
            "- `non_uniform_scale_with_collider` — GameObject with a collider and non-uniform lossyScale.\n" +
            "- `mixed_2d_3d_physics` — GameObject hosts both 3D and 2D physics components.\n" +
            "- `self_layer_collision_disabled` — a populated layer is set to NOT collide with itself.\n" +
            "- `missing_physics_material` — collider without a `sharedMaterial` assigned (Info).")]
        [Description("Scan all loaded scenes for common physics misconfigurations and return a list of issues.")]
        public ValidationResult Validate
        (
            [Description("When true, also emit Info-severity findings. Default false.")]
            bool includeInfo = false
        )
        {
            return MainThread.Instance.Run(() =>
            {
                var issues = new List<PhysicsValidationIssue>();
                int scanned = 0;

                foreach (var root in EnumerateLoadedSceneRoots())
                    ValidateRecursive(root, issues, includeInfo, ref scanned);

                ValidateCollisionMatrix(issues);

                var summary = new Dictionary<string, int>();
                foreach (var issue in issues)
                {
                    if (!summary.TryGetValue(issue.Code, out int count))
                        count = 0;
                    summary[issue.Code] = count + 1;
                }

                return new ValidationResult
                {
                    Ok = true,
                    Issues = issues,
                    ObjectsScanned = scanned,
                    Summary = summary
                };
            });
        }

        // -----------------------------------------------------------------
        // Internal helpers
        // -----------------------------------------------------------------

        private static IEnumerable<GameObject> EnumerateLoadedSceneRoots()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                    yield return root;
            }
        }

        private static void ValidateRecursive(GameObject go, List<PhysicsValidationIssue> issues, bool includeInfo, ref int scanned)
        {
            ValidateOne(go, issues, includeInfo);
            scanned++;
            var t = go.transform;
            for (int i = 0; i < t.childCount; i++)
                ValidateRecursive(t.GetChild(i).gameObject, issues, includeInfo, ref scanned);
        }

        private static void ValidateOne(GameObject go, List<PhysicsValidationIssue> issues, bool includeInfo)
        {
            string scenePath = GetHierarchyPath(go);

            // ---- 3D colliders / rigidbodies ---------------------------------
            var rb3D = go.GetComponent<Rigidbody>();
            var colliders3D = go.GetComponents<Collider>();

            if (colliders3D.Length > 0 && rb3D == null && !go.isStatic)
            {
                // Static-collider warning when no Rigidbody on any ancestor either.
                if (!HasRigidbodyInParent(go))
                {
                    issues.Add(new PhysicsValidationIssue
                    {
                        Severity = PhysicsValidationSeverity.Warning,
                        Code = Code_ColliderNoRigidbody,
                        Path = scenePath,
                        Message = $"'{go.name}' has a Collider but no Rigidbody on itself or any parent. " +
                                  "Moving the Transform of a static collider triggers a broadphase rebuild every frame."
                    });
                }
            }

            if (rb3D != null)
            {
                if (rb3D.mass <= 0f)
                {
                    issues.Add(new PhysicsValidationIssue
                    {
                        Severity = PhysicsValidationSeverity.Error,
                        Code = Code_RigidbodyMassZero,
                        Path = scenePath,
                        Message = $"Rigidbody on '{go.name}' has mass {rb3D.mass}. The physics engine becomes unstable for non-positive mass."
                    });
                }

                if (!rb3D.isKinematic)
                {
                    foreach (var mc in go.GetComponents<MeshCollider>())
                    {
                        if (!mc.convex)
                        {
                            issues.Add(new PhysicsValidationIssue
                            {
                                Severity = PhysicsValidationSeverity.Error,
                                Code = Code_NonConvexMeshOnDynamic,
                                Path = scenePath,
                                Message = $"MeshCollider on '{go.name}' must be Convex when the host Rigidbody is non-kinematic."
                            });
                        }
                    }
                }
            }

            // ---- 2D colliders / rigidbodies ---------------------------------
            var rb2D = go.GetComponent<Rigidbody2D>();
            var colliders2D = go.GetComponents<Collider2D>();

            if (colliders2D.Length > 0 && rb2D == null && !go.isStatic && !HasRigidbody2DInParent(go))
            {
                issues.Add(new PhysicsValidationIssue
                {
                    Severity = PhysicsValidationSeverity.Warning,
                    Code = Code_ColliderNoRigidbody,
                    Path = scenePath,
                    Message = $"'{go.name}' has a Collider2D but no Rigidbody2D on itself or any parent."
                });
            }

            if (rb2D != null && rb2D.mass <= 0f)
            {
                issues.Add(new PhysicsValidationIssue
                {
                    Severity = PhysicsValidationSeverity.Error,
                    Code = Code_Rigidbody2DMassZero,
                    Path = scenePath,
                    Message = $"Rigidbody2D on '{go.name}' has mass {rb2D.mass}."
                });
            }

            // ---- Non-uniform scale + collider ------------------------------
            bool hasAnyCollider = colliders3D.Length > 0 || colliders2D.Length > 0;
            if (hasAnyCollider)
            {
                var s = go.transform.lossyScale;
                if (Mathf.Abs(s.x - s.y) > 0.01f || Mathf.Abs(s.y - s.z) > 0.01f)
                {
                    issues.Add(new PhysicsValidationIssue
                    {
                        Severity = PhysicsValidationSeverity.Warning,
                        Code = Code_NonUniformScale,
                        Path = scenePath,
                        Message = $"'{go.name}' has non-uniform scale ({s.x:F2}, {s.y:F2}, {s.z:F2}) which degrades physics performance."
                    });
                }
            }

            // ---- Mixed 2D/3D -----------------------------------------------
            bool has3D = rb3D != null || colliders3D.Length > 0;
            bool has2D = rb2D != null || colliders2D.Length > 0;
            if (has3D && has2D)
            {
                issues.Add(new PhysicsValidationIssue
                {
                    Severity = PhysicsValidationSeverity.Warning,
                    Code = Code_Mixed2D3D,
                    Path = scenePath,
                    Message = $"'{go.name}' hosts both 3D and 2D physics components. 2D and 3D physics do not interact."
                });
            }

            // ---- Missing physics material (Info) ---------------------------
            if (includeInfo)
            {
                foreach (var c in colliders3D)
                {
                    if (c.sharedMaterial == null)
                    {
                        issues.Add(new PhysicsValidationIssue
                        {
                            Severity = PhysicsValidationSeverity.Info,
                            Code = Code_MissingPhysicsMaterial,
                            Path = scenePath,
                            Message = $"Collider {c.GetType().Name} on '{go.name}' has no physics material (using engine defaults)."
                        });
                    }
                }
                foreach (var c in colliders2D)
                {
                    if (c.sharedMaterial == null)
                    {
                        issues.Add(new PhysicsValidationIssue
                        {
                            Severity = PhysicsValidationSeverity.Info,
                            Code = Code_MissingPhysicsMaterial,
                            Path = scenePath,
                            Message = $"Collider2D {c.GetType().Name} on '{go.name}' has no physics material (using engine defaults)."
                        });
                    }
                }
            }
        }

        private static void ValidateCollisionMatrix(List<PhysicsValidationIssue> issues)
        {
            for (int i = 0; i < 32; i++)
            {
                var name = LayerMask.LayerToName(i);
                if (string.IsNullOrEmpty(name)) continue;

                if (UnityEngine.Physics.GetIgnoreLayerCollision(i, i))
                {
                    issues.Add(new PhysicsValidationIssue
                    {
                        Severity = PhysicsValidationSeverity.Warning,
                        Code = Code_SelfLayerCollisionDisabled,
                        Path = string.Empty,
                        Message = $"3D layer '{name}' (index {i}) is configured to NOT collide with itself. This is usually a misconfiguration."
                    });
                }
                if (Physics2D.GetIgnoreLayerCollision(i, i))
                {
                    issues.Add(new PhysicsValidationIssue
                    {
                        Severity = PhysicsValidationSeverity.Warning,
                        Code = Code_SelfLayerCollisionDisabled,
                        Path = string.Empty,
                        Message = $"2D layer '{name}' (index {i}) is configured to NOT collide with itself."
                    });
                }
            }
        }

        private static bool HasRigidbodyInParent(GameObject go)
        {
            var t = go.transform.parent;
            while (t != null)
            {
                if (t.GetComponent<Rigidbody>() != null) return true;
                t = t.parent;
            }
            return false;
        }

        private static bool HasRigidbody2DInParent(GameObject go)
        {
            var t = go.transform.parent;
            while (t != null)
            {
                if (t.GetComponent<Rigidbody2D>() != null) return true;
                t = t.parent;
            }
            return false;
        }

        private static string GetHierarchyPath(GameObject go)
        {
            var stack = new List<string>(8);
            var t = go.transform;
            while (t != null)
            {
                stack.Add(t.name);
                t = t.parent;
            }
            stack.Reverse();
            return string.Join("/", stack);
        }
    }
}
