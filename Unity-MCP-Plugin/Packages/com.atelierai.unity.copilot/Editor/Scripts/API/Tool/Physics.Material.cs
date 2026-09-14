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
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using com.AtelierAI.Uco.Framework;
using com.IvanMurzak.ReflectorNet.Utils;
using com.AtelierAI.Unity.Copilot.Runtime.Extensions;
using UnityEditor;
using UnityEngine;

#if UNITY_6000_0_OR_NEWER
using UnityPhysicsMaterial = UnityEngine.PhysicsMaterial;
using UnityPhysicsMaterialCombine = UnityEngine.PhysicsMaterialCombine;
#else
using UnityPhysicsMaterial = UnityEngine.PhysicMaterial;
using UnityPhysicsMaterialCombine = UnityEngine.PhysicMaterialCombine;
#endif

namespace AIGD
{
    [Description("Result of a 'physics-material-create', '-configure' or '-assign' call.")]
    public class PhysicsMaterialResult
    {
        [Description("True when the operation completed without errors.")]
        public bool Ok { get; set; }
        [Description("Error message when Ok is false. Null on success.")]
        public string? Error { get; set; }

        [Description("Asset path of the affected physics material.")]
        public string? Path { get; set; }
        [Description("Dimension of the affected material: '2d' or '3d'.")]
        public string Dimension { get; set; } = "3d";

        [Description("3D dynamic friction (null for 2D materials).")]
        public float? DynamicFriction { get; set; }
        [Description("3D static friction (null for 2D materials).")]
        public float? StaticFriction { get; set; }
        [Description("2D combined friction (null for 3D materials).")]
        public float? Friction { get; set; }
        [Description("Bounciness (both dimensions).")]
        public float? Bounciness { get; set; }
        [Description("Friction-combine mode name (3D only). Average, Minimum, Multiply, Maximum.")]
        public string? FrictionCombine { get; set; }
        [Description("Bounce-combine mode name (3D only). Average, Minimum, Multiply, Maximum.")]
        public string? BounceCombine { get; set; }

        [Description("Names of properties mutated by configure / create. Empty for assign.")]
        public List<string> Changed { get; set; } = new List<string>();

        [Description("For assign: name of the GameObject the material was assigned to.")]
        public string? AssignedGameObject { get; set; }
        [Description("For assign: name of the collider component the material was assigned to.")]
        public string? AssignedCollider { get; set; }
    }
}

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    using AIGD;

    public partial class Tool_Physics
    {
        public const string PhysicsMaterialCreateToolId = "physics-material-create";
        public const string PhysicsMaterialConfigureToolId = "physics-material-configure";
        public const string PhysicsMaterialAssignToolId = "physics-material-assign";

        private const string Extension3D = ".physicMaterial";
        private const string Extension2D = ".physicsMaterial2D";

        // -----------------------------------------------------------------
        // Create
        // -----------------------------------------------------------------

        [UcoTool
        (
            PhysicsMaterialCreateToolId,
            Title = "Physics / Material / Create",
            DestructiveHint = true
        )]
        [UcoSkillDescription("Create a new physics material asset (3D or 2D). The path must be under " +
            "'Assets/' and use the correct extension for the dimension ('.physicMaterial' for 3D, " +
            "'.physicsMaterial2D' for 2D). Use 'overwrite: true' to replace an existing asset.")]
        [UcoSkillBody("Creates a `PhysicsMaterial` (3D) or `PhysicsMaterial2D` asset.\n\n" +
            "## Inputs\n\n" +
            "- `path` — required asset path under `Assets/` ending in `.physicMaterial` (3D) or `.physicsMaterial2D` (2D).\n" +
            "- `dimension` (default `'3d'`) — `'2d'` or `'3d'`.\n" +
            "- 3D: `dynamicFriction` (default 0.6), `staticFriction` (default 0.6), `bounciness` (default 0), " +
            "`frictionCombine` / `bounceCombine` (Average/Minimum/Multiply/Maximum).\n" +
            "- 2D: `friction` (default 0.4), `bounciness` (default 0).\n" +
            "- `overwrite` (default false) — when true, replaces an existing asset at the same path.\n\n" +
            "## Unity-version notes\n\n" +
            "Unity 6 renamed `PhysicMaterial` to `PhysicsMaterial` and `PhysicMaterialCombine` to " +
            "`PhysicsMaterialCombine`. Both APIs are routed through the same type aliases in this implementation, " +
            "so the public surface is identical across versions.")]
        [Description("Create a new physics material asset (3D or 2D). " +
            "Path must live under 'Assets/' and end with '.physicMaterial' (3D) or '.physicsMaterial2D' (2D).")]
        public PhysicsMaterialResult CreateMaterial
        (
            [Description("Asset path under 'Assets/'. Must end with '.physicMaterial' (3D) or '.physicsMaterial2D' (2D).")]
            string path,
            [Description("Dimension: '3d' (default) or '2d'.")]
            string? dimension = null,
            [Description("Dynamic friction (3D only). Default 0.6.")]
            float? dynamicFriction = null,
            [Description("Static friction (3D only). Default 0.6.")]
            float? staticFriction = null,
            [Description("Combined friction value (2D only). Default 0.4.")]
            float? friction = null,
            [Description("Bounciness. Default 0. Applies to both dimensions.")]
            float? bounciness = null,
            [Description("Friction combine mode (3D only): Average, Minimum, Multiply, Maximum.")]
            string? frictionCombine = null,
            [Description("Bounce combine mode (3D only): Average, Minimum, Multiply, Maximum.")]
            string? bounceCombine = null,
            [Description("If true, overwrite an existing asset at the same path. Default false.")]
            bool overwrite = false
        )
        {
            if (string.IsNullOrEmpty(path))
                return new PhysicsMaterialResult { Ok = false, Error = Error.EmptyAssetPath() };

            if (!path.StartsWith("Assets/"))
                return new PhysicsMaterialResult { Ok = false, Error = Error.AssetPathMustStartWithAssets(path) };

            if (!TryParseDimension(dimension, out var dim, out var dimErr))
                return new PhysicsMaterialResult { Ok = false, Error = dimErr };

            if (Is2D(dim))
            {
                if (!path.EndsWith(Extension2D, StringComparison.OrdinalIgnoreCase))
                    return new PhysicsMaterialResult { Ok = false, Error = Error.AssetPathExtensionMismatch2D(path) };
            }
            else
            {
                if (!path.EndsWith(Extension3D, StringComparison.OrdinalIgnoreCase))
                    return new PhysicsMaterialResult { Ok = false, Error = Error.AssetPathExtensionMismatch3D(path) };
            }

            return MainThread.Instance.Run(() =>
            {
                EnsureFolderExistsForAsset(path);

                var existing = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
                if (existing != null)
                {
                    if (!overwrite)
                        return new PhysicsMaterialResult { Ok = false, Error = Error.MaterialAlreadyExists(path) };

                    AssetDatabase.DeleteAsset(path);
                }

                if (Is2D(dim))
                    return Create2DMaterial(path, friction, bounciness);

                return Create3DMaterial(path, dynamicFriction, staticFriction, bounciness,
                    frictionCombine, bounceCombine);
            });
        }

        // -----------------------------------------------------------------
        // Configure
        // -----------------------------------------------------------------

        [UcoTool
        (
            PhysicsMaterialConfigureToolId,
            Title = "Physics / Material / Configure",
            DestructiveHint = true
        )]
        [UcoSkillDescription("Mutate properties of an existing physics material asset. Any argument left " +
            "null is ignored; only the non-null arguments are applied.")]
        [UcoSkillBody("Loads the material at `path` and writes the supplied fields. The dimension is " +
            "inferred from the file extension when not provided explicitly.\n\n" +
            "## Inputs\n\n" +
            "- `path` — required asset path.\n" +
            "- `dimension` (optional) — override inferred dimension. Useful when the same asset name is shared " +
            "across both dimensions in different folders.\n" +
            "- 3D-only: `dynamicFriction`, `staticFriction`, `frictionCombine`, `bounceCombine`.\n" +
            "- 2D-only: `friction`.\n" +
            "- Both: `bounciness`.\n\n" +
            "## Behavior\n\n" +
            "After updating the material the asset is marked dirty and saved.")]
        [Description("Mutate properties of an existing physics material asset.")]
        public PhysicsMaterialResult ConfigureMaterial
        (
            [Description("Asset path of the existing physics material.")]
            string path,
            [Description("Dimension override: '3d' or '2d'. If omitted, inferred from the file extension.")]
            string? dimension = null,
            [Description("Dynamic friction (3D only).")]
            float? dynamicFriction = null,
            [Description("Static friction (3D only).")]
            float? staticFriction = null,
            [Description("Combined friction value (2D only).")]
            float? friction = null,
            [Description("Bounciness. Applies to both dimensions.")]
            float? bounciness = null,
            [Description("Friction combine mode (3D only): Average, Minimum, Multiply, Maximum.")]
            string? frictionCombine = null,
            [Description("Bounce combine mode (3D only): Average, Minimum, Multiply, Maximum.")]
            string? bounceCombine = null
        )
        {
            if (string.IsNullOrEmpty(path))
                return new PhysicsMaterialResult { Ok = false, Error = Error.EmptyAssetPath() };

            PhysicsDimension dim;
            if (!string.IsNullOrEmpty(dimension))
            {
                if (!TryParseDimension(dimension, out dim, out var dimErr))
                    return new PhysicsMaterialResult { Ok = false, Error = dimErr };
            }
            else
            {
                if (path.EndsWith(Extension2D, StringComparison.OrdinalIgnoreCase))
                    dim = PhysicsDimension.TwoD;
                else if (path.EndsWith(Extension3D, StringComparison.OrdinalIgnoreCase))
                    dim = PhysicsDimension.ThreeD;
                else
                    return new PhysicsMaterialResult { Ok = false, Error = Error.MaterialUnknownDimension(path) };
            }

            return MainThread.Instance.Run(() =>
            {
                if (Is2D(dim))
                    return Configure2DMaterial(path, friction, bounciness);

                return Configure3DMaterial(path, dynamicFriction, staticFriction, bounciness,
                    frictionCombine, bounceCombine);
            });
        }

        // -----------------------------------------------------------------
        // Assign
        // -----------------------------------------------------------------

        [UcoTool
        (
            PhysicsMaterialAssignToolId,
            Title = "Physics / Material / Assign",
            DestructiveHint = true
        )]
        [UcoSkillDescription("Assign a physics material asset to the first matching collider on a " +
            "GameObject. The dimension of the material (3D vs 2D) is inferred from the asset type and used " +
            "to pick the matching collider component.")]
        [UcoSkillBody("Loads the material at `materialPath`, finds a compatible collider on the target " +
            "GameObject, and assigns it via `Collider.sharedMaterial` (3D) or `Collider2D.sharedMaterial` (2D).\n\n" +
            "## Inputs\n\n" +
            "- `target` — GameObjectRef of the host GameObject (path, name, or instance ID).\n" +
            "- `materialPath` — asset path of the physics material to assign.\n\n" +
            "## Behavior\n\n" +
            "If the GameObject has no matching collider an `Error` is returned. The first collider of the " +
            "matching dimension is used.")]
        [Description("Assign a physics material asset to a collider on a GameObject.")]
        public PhysicsMaterialResult AssignMaterial
        (
            [Description("Target GameObject to receive the physics material.")]
            GameObjectRef target,
            [Description("Asset path of the physics material to assign.")]
            string materialPath
        )
        {
            if (target == null)
                return new PhysicsMaterialResult { Ok = false, Error = Error.GameObjectRefIsNull() };
            if (!target.IsValid(out var refErr))
                return new PhysicsMaterialResult { Ok = false, Error = refErr };
            if (string.IsNullOrEmpty(materialPath))
                return new PhysicsMaterialResult { Ok = false, Error = Error.EmptyAssetPath() };

            return MainThread.Instance.Run(() =>
            {
                var go = target.FindGameObject(out var goErr);
                if (go == null)
                    return new PhysicsMaterialResult { Ok = false, Error = goErr ?? "GameObject not found." };

                var mat3D = AssetDatabase.LoadAssetAtPath<UnityPhysicsMaterial>(materialPath);
                var mat2D = AssetDatabase.LoadAssetAtPath<PhysicsMaterial2D>(materialPath);

                if (mat3D == null && mat2D == null)
                    return new PhysicsMaterialResult { Ok = false, Error = Error.MaterialUnknownDimension(materialPath) };

                if (mat3D != null)
                {
                    var collider3D = go.GetComponent<Collider>();
                    if (collider3D == null)
                        return new PhysicsMaterialResult { Ok = false, Error = Error.NoColliderOnGameObject(go.name) };

                    Undo.RecordObject(collider3D, "Assign Physics Material");
                    collider3D.sharedMaterial = mat3D;
                    EditorUtility.SetDirty(collider3D);

                    return new PhysicsMaterialResult
                    {
                        Ok = true,
                        Path = materialPath,
                        Dimension = "3d",
                        AssignedGameObject = go.name,
                        AssignedCollider = collider3D.GetType().Name
                    };
                }
                else
                {
                    var collider2D = go.GetComponent<Collider2D>();
                    if (collider2D == null)
                        return new PhysicsMaterialResult { Ok = false, Error = Error.NoColliderOnGameObject(go.name) };

                    Undo.RecordObject(collider2D, "Assign Physics Material 2D");
                    collider2D.sharedMaterial = mat2D;
                    EditorUtility.SetDirty(collider2D);

                    return new PhysicsMaterialResult
                    {
                        Ok = true,
                        Path = materialPath,
                        Dimension = "2d",
                        AssignedGameObject = go.name,
                        AssignedCollider = collider2D!.GetType().Name
                    };
                }
            });
        }

        // -----------------------------------------------------------------
        // Internal helpers
        // -----------------------------------------------------------------

        private static PhysicsMaterialResult Create3DMaterial(
            string path,
            float? dynamicFriction,
            float? staticFriction,
            float? bounciness,
            string? frictionCombine,
            string? bounceCombine)
        {
            var mat = new UnityPhysicsMaterial(Path.GetFileNameWithoutExtension(path))
            {
                dynamicFriction = dynamicFriction ?? 0.6f,
                staticFriction = staticFriction ?? 0.6f,
                bounciness = bounciness ?? 0f
            };

            if (!string.IsNullOrEmpty(frictionCombine))
            {
                if (!Enum.TryParse<UnityPhysicsMaterialCombine>(frictionCombine, true, out var fc))
                    return new PhysicsMaterialResult { Ok = false, Error = Error.InvalidFrictionCombine(frictionCombine!) };
                mat.frictionCombine = fc;
            }

            if (!string.IsNullOrEmpty(bounceCombine))
            {
                if (!Enum.TryParse<UnityPhysicsMaterialCombine>(bounceCombine, true, out var bc))
                    return new PhysicsMaterialResult { Ok = false, Error = Error.InvalidBounceCombine(bounceCombine!) };
                mat.bounceCombine = bc;
            }

            AssetDatabase.CreateAsset(mat, path);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

            return new PhysicsMaterialResult
            {
                Ok = true,
                Path = path,
                Dimension = "3d",
                DynamicFriction = mat.dynamicFriction,
                StaticFriction = mat.staticFriction,
                Bounciness = mat.bounciness,
                FrictionCombine = mat.frictionCombine.ToString(),
                BounceCombine = mat.bounceCombine.ToString()
            };
        }

        private static PhysicsMaterialResult Create2DMaterial(string path, float? friction, float? bounciness)
        {
            var mat = new PhysicsMaterial2D(Path.GetFileNameWithoutExtension(path))
            {
                friction = friction ?? 0.4f,
                bounciness = bounciness ?? 0f
            };

            AssetDatabase.CreateAsset(mat, path);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

            return new PhysicsMaterialResult
            {
                Ok = true,
                Path = path,
                Dimension = "2d",
                Friction = mat.friction,
                Bounciness = mat.bounciness
            };
        }

        private static PhysicsMaterialResult Configure3DMaterial(
            string path,
            float? dynamicFriction,
            float? staticFriction,
            float? bounciness,
            string? frictionCombine,
            string? bounceCombine)
        {
            var mat = AssetDatabase.LoadAssetAtPath<UnityPhysicsMaterial>(path);
            if (mat == null)
                return new PhysicsMaterialResult { Ok = false, Error = Error.Material3DNotFound(path) };

            Undo.RecordObject(mat, "Configure Physics Material");

            var changed = new List<string>();
            if (dynamicFriction.HasValue) { mat.dynamicFriction = dynamicFriction.Value; changed.Add("dynamicFriction"); }
            if (staticFriction.HasValue) { mat.staticFriction = staticFriction.Value; changed.Add("staticFriction"); }
            if (bounciness.HasValue) { mat.bounciness = bounciness.Value; changed.Add("bounciness"); }
            if (!string.IsNullOrEmpty(frictionCombine))
            {
                if (!Enum.TryParse<UnityPhysicsMaterialCombine>(frictionCombine, true, out var fc))
                    return new PhysicsMaterialResult { Ok = false, Error = Error.InvalidFrictionCombine(frictionCombine!) };
                mat.frictionCombine = fc;
                changed.Add("frictionCombine");
            }
            if (!string.IsNullOrEmpty(bounceCombine))
            {
                if (!Enum.TryParse<UnityPhysicsMaterialCombine>(bounceCombine, true, out var bc))
                    return new PhysicsMaterialResult { Ok = false, Error = Error.InvalidBounceCombine(bounceCombine!) };
                mat.bounceCombine = bc;
                changed.Add("bounceCombine");
            }

            EditorUtility.SetDirty(mat);
            AssetDatabase.SaveAssets();

            return new PhysicsMaterialResult
            {
                Ok = true,
                Path = path,
                Dimension = "3d",
                DynamicFriction = mat.dynamicFriction,
                StaticFriction = mat.staticFriction,
                Bounciness = mat.bounciness,
                FrictionCombine = mat.frictionCombine.ToString(),
                BounceCombine = mat.bounceCombine.ToString(),
                Changed = changed
            };
        }

        private static PhysicsMaterialResult Configure2DMaterial(string path, float? friction, float? bounciness)
        {
            var mat = AssetDatabase.LoadAssetAtPath<PhysicsMaterial2D>(path);
            if (mat == null)
                return new PhysicsMaterialResult { Ok = false, Error = Error.Material2DNotFound(path) };

            Undo.RecordObject(mat, "Configure Physics Material 2D");

            var changed = new List<string>();
            if (friction.HasValue) { mat.friction = friction.Value; changed.Add("friction"); }
            if (bounciness.HasValue) { mat.bounciness = bounciness.Value; changed.Add("bounciness"); }

            EditorUtility.SetDirty(mat);
            AssetDatabase.SaveAssets();

            return new PhysicsMaterialResult
            {
                Ok = true,
                Path = path,
                Dimension = "2d",
                Friction = mat.friction,
                Bounciness = mat.bounciness,
                Changed = changed
            };
        }

        private static void EnsureFolderExistsForAsset(string assetPath)
        {
            var directory = Path.GetDirectoryName(assetPath);
            if (string.IsNullOrEmpty(directory)) return;
            if (Directory.Exists(directory)) return;
            Directory.CreateDirectory(directory);
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        }
    }
}
