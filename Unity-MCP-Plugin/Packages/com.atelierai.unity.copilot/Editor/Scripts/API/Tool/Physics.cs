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
using com.AtelierAI.Uco.Framework;
using UnityEditor;
using UnityEngine;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    /// <summary>
    /// Physics dimension selector — controls whether a tool targets 3D physics
    /// (<see cref="UnityEngine.Physics"/>) or 2D physics (<see cref="UnityEngine.Physics2D"/>).
    /// </summary>
    public enum PhysicsDimension
    {
        ThreeD = 0,
        TwoD = 1
    }

    [UcoToolType]
    public partial class Tool_Physics
    {
        // -----------------------------------------------------------------
        // Shared helpers
        // -----------------------------------------------------------------

        /// <summary>
        /// Parse the user-supplied <c>dimension</c> string into a <see cref="PhysicsDimension"/>.
        /// Accepts <c>"3d"</c> / <c>"3D"</c> (default when null/empty) and <c>"2d"</c> / <c>"2D"</c>.
        /// Returns false and populates <paramref name="error"/> for any other value.
        /// </summary>
        public static bool TryParseDimension(string? raw, out PhysicsDimension dimension, out string? error)
        {
            if (string.IsNullOrEmpty(raw))
            {
                dimension = PhysicsDimension.ThreeD;
                error = null;
                return true;
            }

            switch (raw!.Trim().ToLowerInvariant())
            {
                case "3d":
                case "3":
                case "three":
                case "threed":
                    dimension = PhysicsDimension.ThreeD;
                    error = null;
                    return true;
                case "2d":
                case "2":
                case "two":
                case "twod":
                    dimension = PhysicsDimension.TwoD;
                    error = null;
                    return true;
                default:
                    dimension = PhysicsDimension.ThreeD;
                    error = Error.InvalidDimension(raw);
                    return false;
            }
        }

        /// <summary>Convenience predicate: true when the supplied dimension is 2D.</summary>
        public static bool Is2D(PhysicsDimension dimension) => dimension == PhysicsDimension.TwoD;

        /// <summary>Stringify a <see cref="PhysicsDimension"/> as the canonical lowercase token (<c>"2d"</c> / <c>"3d"</c>).</summary>
        public static string DimensionToString(PhysicsDimension dimension) => dimension == PhysicsDimension.TwoD ? "2d" : "3d";

        /// <summary>
        /// Resolve a layer token (either an integer index or a layer name) into a 0..31 index.
        /// Returns -1 when the layer cannot be resolved or the index is out of range.
        /// </summary>
        public static int ResolveLayer(string? layerToken)
        {
            if (string.IsNullOrEmpty(layerToken))
                return -1;

            var raw = layerToken!.Trim();
            if (int.TryParse(raw, out int parsed))
                return (parsed >= 0 && parsed < 32) ? parsed : -1;

            int idx = LayerMask.NameToLayer(raw);
            return (idx >= 0 && idx < 32) ? idx : -1;
        }

        /// <summary>
        /// Mark a ProjectSettings asset dirty so the change persists. Used by Physics.Settings.cs
        /// and Physics.CollisionMatrix.cs after applying mutations.
        /// </summary>
        internal static void MarkProjectSettingsDirty(string projectSettingsAssetPath)
        {
            if (string.IsNullOrEmpty(projectSettingsAssetPath))
                return;

            var assets = AssetDatabase.LoadAllAssetsAtPath(projectSettingsAssetPath);
            if (assets != null && assets.Length > 0 && assets[0] != null)
                EditorUtility.SetDirty(assets[0]);
        }

        // -----------------------------------------------------------------
        // Error messages
        //
        // Centralised here so every Physics.* tool partial reads from the
        // same source. New error helpers should be added here, not inside
        // individual tool files.
        //
        // The sections are organised by which sub-tool group consumes them:
        //   1. Shared / dimension / layer
        //   2. Settings              (Physics.Settings.cs)
        //   3. Collision matrix      (Physics.CollisionMatrix.cs)
        //   4. Physics materials     (Physics.Material.cs)
        //   5. Rigidbody             (Physics.Rigidbody.cs)
        //   6. Validation            (Physics.Validate.cs)
        //   7. Joints                (Physics.Joint*.cs — Physics B agent)
        //   8. Queries / forces      (Physics.Raycast/.../Overlap/Force/Simulate.cs — Physics C agent)
        //
        // Physics B / Physics C agents MUST consume the helpers in sections
        // 7 and 8 below — do not add Error helpers in those files.
        // -----------------------------------------------------------------
        public static class Error
        {
            // ---- 1. Shared / dimension / layer ---------------------------

            public static string InvalidDimension(string raw)
                => $"Invalid 'dimension' value: '{raw}'. Use '3d' (default) or '2d'.";

            public static string LayerOutOfRange(int layer)
                => $"Layer index '{layer}' is out of range. Valid range: 0..31.";

            public static string LayerNotFound(string token)
                => $"Layer '{token}' was not found. Provide a known layer name (see Project Settings ▸ Tags and Layers) " +
                   $"or an integer index in the 0..31 range.";

            public static string GameObjectRefIsNull()
                => "'target' parameter is required. Provide a non-null GameObjectRef (path, name or instanceID).";

            // ---- 2. Settings (Physics.Settings.cs) -----------------------

            public static string Unknown3DSettings(string keys)
                => $"Unknown 3D physics setting(s): {keys}.";

            public static string Unknown2DSettings(string keys)
                => $"Unknown 2D physics setting(s): {keys}.";

            public static string GravityRequires3Components()
                => "3D gravity requires a Vector3 (x, y, z).";

            public static string GravityRequires2Components()
                => "2D gravity requires a Vector2 (x, y).";

            public static string NoSettingsApplied()
                => "No applicable settings were supplied for the requested dimension.";

            public static string Setting2DOnly(string name)
                => $"Setting '{name}' is only valid for the 2D dimension.";

            public static string Setting3DOnly(string name)
                => $"Setting '{name}' is only valid for the 3D dimension.";

            // ---- 3. Collision matrix (Physics.CollisionMatrix.cs) --------

            public static string LayerAUnresolved(string token)
                => $"Could not resolve 'layerA' = '{token}'. Provide a known layer name or an index 0..31.";

            public static string LayerBUnresolved(string token)
                => $"Could not resolve 'layerB' = '{token}'. Provide a known layer name or an index 0..31.";

            public static string LayerSelfToggle(int layer)
                => $"Toggling collision for layer {layer} against itself is allowed but unusual — " +
                   $"the engine still processes self-collision rules from the same layer when this is enabled.";

            // ---- 4. Physics materials (Physics.Material.cs) --------------

            public static string EmptyAssetPath()
                => "Physics material 'path' is empty. Provide a path under 'Assets/' ending with " +
                   "'.physicMaterial' (3D) or '.physicsMaterial2D' (2D).";

            public static string AssetPathMustStartWithAssets(string path)
                => $"Physics material path must start with 'Assets/'. Path: '{path}'.";

            public static string AssetPathExtensionMismatch3D(string path)
                => $"Path must end with '.physicMaterial' for a 3D physics material. Path: '{path}'.";

            public static string AssetPathExtensionMismatch2D(string path)
                => $"Path must end with '.physicsMaterial2D' for a 2D physics material. Path: '{path}'.";

            public static string MaterialAlreadyExists(string path)
                => $"A physics material already exists at '{path}'. Pass 'overwrite: true' to replace it, or use 'physics-material-configure' to modify it.";

            public static string Material3DNotFound(string path)
                => $"No 3D physics material found at: '{path}'.";

            public static string Material2DNotFound(string path)
                => $"No 2D physics material found at: '{path}'.";

            public static string MaterialUnknownDimension(string path)
                => $"Asset at '{path}' is neither a 3D nor 2D physics material.";

            public static string InvalidFrictionCombine(string raw)
                => $"Invalid 'frictionCombine' value: '{raw}'. Valid: Average, Minimum, Multiply, Maximum.";

            public static string InvalidBounceCombine(string raw)
                => $"Invalid 'bounceCombine' value: '{raw}'. Valid: Average, Minimum, Multiply, Maximum.";

            public static string NoColliderOnGameObject(string goName)
                => $"GameObject '{goName}' has no Collider / Collider2D to receive the physics material.";

            public static string ColliderTypeMismatch(string goName, string requested, string actualDim)
                => $"Collider '{requested}' on '{goName}' does not match the requested dimension ({actualDim}).";

            public static string FrictionRequiredFor2D()
                => "2D physics materials use a single combined 'friction' value; supply 'friction' instead of 'dynamicFriction' / 'staticFriction'.";

            // ---- 5. Rigidbody (Physics.Rigidbody.cs) ---------------------

            public static string NoRigidbodyOnGameObject(string goName)
                => $"No Rigidbody or Rigidbody2D found on '{goName}'.";

            public static string NoRigidbody3DOnGameObject(string goName)
                => $"No 3D Rigidbody found on '{goName}'.";

            public static string NoRigidbody2DOnGameObject(string goName)
                => $"No 2D Rigidbody2D found on '{goName}'.";

            public static string InvalidInterpolation(string raw)
                => $"Invalid 'interpolation' value: '{raw}'. Valid: None, Interpolate, Extrapolate.";

            public static string InvalidCollisionDetection3D(string raw)
                => $"Invalid 'collisionDetection' value: '{raw}'. Valid (3D): Discrete, Continuous, ContinuousDynamic, ContinuousSpeculative.";

            public static string InvalidCollisionDetection2D(string raw)
                => $"Invalid 'collisionDetection' value: '{raw}'. Valid (2D): Discrete, Continuous.";

            public static string InvalidBodyType2D(string raw)
                => $"Invalid 'bodyType' value: '{raw}'. Valid (2D): Dynamic, Kinematic, Static.";

            public static string InvalidConstraints3D(string raw)
                => $"Invalid 'constraints' value: '{raw}'. Use a comma-separated list of RigidbodyConstraints flags " +
                   $"(e.g. 'FreezePositionX, FreezeRotationY') or an integer bitmask.";

            public static string InvalidConstraints2D(string raw)
                => $"Invalid 'constraints' value: '{raw}'. Use a comma-separated list of RigidbodyConstraints2D flags " +
                   $"(e.g. 'FreezePositionX, FreezeRotation') or an integer bitmask.";

            // ---- 6. Validation (Physics.Validate.cs) ---------------------

            public static string ValidationTargetNotFound(string token)
                => $"Validation target '{token}' was not found in the active scene.";

            public static string ValidationPageSizeOutOfRange(int pageSize)
                => $"page_size '{pageSize}' is out of range. Use a positive integer (default 50).";

            // ---- 7. Joints (used by Physics.Joint*.cs in Physics B) ------
            // These helpers exist so the Physics B agent does not need to
            // edit this file. Add new joint-related strings here ONLY when
            // they are reused by more than one Physics.Joint*.cs partial.

            public static string InvalidJointType3D(string raw)
                => $"Invalid 3D joint type: '{raw}'. Valid: fixed, hinge, spring, character, configurable.";

            public static string InvalidJointType2D(string raw)
                => $"Invalid 2D joint type: '{raw}'. Valid: distance, fixed, friction, hinge, relative, slider, spring, target, wheel.";

            public static string JointTypeRequired()
                => "'jointType' parameter is required. See 'physics-joint-add' help for the list of valid types per dimension.";

            public static string NoJointOnGameObject(string goName)
                => $"No matching joint component was found on '{goName}'.";

            public static string ConnectedBodyNotFound(string token)
                => $"Connected body '{token}' could not be resolved to a Rigidbody / Rigidbody2D.";

            public static string JointConnectedBodyDimensionMismatch()
                => "The connected body's physics dimension does not match the joint's dimension.";

            // ---- 8. Queries / forces (used by Physics.Raycast/.../Overlap/Force/Simulate.cs in Physics C)
            // Same convention as section 7 — Physics C agent reads from
            // here; do not add Error helpers inside the query files.

            public static string OriginRequired()
                => "'origin' parameter is required for ray / cast queries.";

            public static string DirectionRequired()
                => "'direction' parameter is required for ray / cast queries.";

            public static string DirectionIsZero()
                => "'direction' must be a non-zero vector.";

            public static string ShapeRequired()
                => "'shape' parameter is required for overlap / shape-cast queries.";

            public static string InvalidShape3D(string raw)
                => $"Invalid 3D shape: '{raw}'. Valid: sphere, box, capsule.";

            public static string InvalidShape2D(string raw)
                => $"Invalid 2D shape: '{raw}'. Valid: circle, box, capsule.";

            public static string InvalidQueryTriggerInteraction(string raw)
                => $"Invalid 'queryTriggerInteraction' value: '{raw}'. Valid: UseGlobal, Ignore, Collide.";

            public static string InvalidForceMode3D(string raw)
                => $"Invalid 'forceMode' value: '{raw}'. Valid (3D): Force, Impulse, Acceleration, VelocityChange.";

            public static string InvalidForceMode2D(string raw)
                => $"Invalid 'forceMode' value: '{raw}'. Valid (2D): Force, Impulse.";

            public static string ForceVectorRequired()
                => "'force' (or 'torque') vector is required for 'physics-force-apply'.";

            public static string ExplosionOnly3D()
                => "Explosion forces ('forceType: explosion') are only supported in 3D physics.";

            public static string ExplosionRadiusRequired()
                => "'explosionRadius' is required for explosion forces.";

            public static string SimulationStepsOutOfRange(int steps)
                => $"'steps' value {steps} is out of range. Provide an integer between 1 and 100.";

            public static string SimulationStepSizeOutOfRange(float stepSize)
                => $"'stepSize' value {stepSize} is out of range. Provide a positive value (typical: 0.001..0.1).";

            public static string SizeRequired()
                => "'size' parameter is required for shape / overlap queries (float radius or [x,y,z] half-extents).";
        }
    }
}
