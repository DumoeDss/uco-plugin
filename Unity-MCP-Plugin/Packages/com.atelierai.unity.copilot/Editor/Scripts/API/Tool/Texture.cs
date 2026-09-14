/*
 * Design inspired by MCP for Unity (CoplayDev/unity-mcp), Copyright (c) Coplay Inc., MIT License.
 * https://github.com/CoplayDev/unity-mcp/blob/main/MCPForUnity/Editor/Tools/ManageTexture.cs
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
using System.IO;
using com.AtelierAI.Uco.Framework;
using UnityEditor;
using UnityEngine;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    /// <summary>
    /// Programmatic Texture2D generation tools. Each tool writes a `.png` file into the
    /// Unity project and imports it through `AssetDatabase`.
    /// </summary>
    [UcoToolType]
    public partial class Tool_Texture
    {
        // Hard caps shared across every texture-create tool. Picked to keep a single
        // RGBA32 buffer below ~64 MB and avoid OOM on large frames.
        public const int MaxDimension = 4096;
        public const int MaxTotalPixels = 16 * 1024 * 1024; // 16 M pixels

        // ----- Shared helpers (used by every Texture.Create*.cs partial) -----

        /// <summary>Throws on empty / wrong-extension / out-of-tree asset paths.</summary>
        internal static void ValidateAssetPathPng(string path, string paramName)
        {
            if (string.IsNullOrEmpty(path))
                throw new ArgumentException(Error.EmptyAssetPath(), paramName);
            if (!path.StartsWith("Assets/", StringComparison.Ordinal))
                throw new ArgumentException(Error.AssetPathMustStartWithAssets(path), paramName);
            if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException(Error.AssetPathMustEndWithPng(path), paramName);
        }

        /// <summary>Throws if `width`/`height` are out of range or the total pixel count blows the budget.</summary>
        internal static void ValidateDimensions(int width, int height)
        {
            if (width < 1 || width > MaxDimension)
                throw new ArgumentException(Error.DimensionOutOfRange("width", width), nameof(width));
            if (height < 1 || height > MaxDimension)
                throw new ArgumentException(Error.DimensionOutOfRange("height", height), nameof(height));
            if ((long)width * height > MaxTotalPixels)
                throw new ArgumentException(Error.TooManyPixels(width, height));
        }

        /// <summary>Throws when any RGBA channel is non-finite or out of [0,1].</summary>
        internal static void ValidateColor(ColorRgba color, string colorParamName)
        {
            if (color == null)
                return;
            ValidateChannel(color.R, colorParamName + ".R");
            ValidateChannel(color.G, colorParamName + ".G");
            ValidateChannel(color.B, colorParamName + ".B");
            ValidateChannel(color.A, colorParamName + ".A");
        }

        private static void ValidateChannel(float value, string fieldName)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
                throw new ArgumentException(Error.ColorChannelNotFinite(fieldName));
            if (value < 0f || value > 1f)
                throw new ArgumentException(Error.ColorChannelOutOfRange(fieldName, value));
        }

        /// <summary>
        /// Creates intermediate Unity folders so `AssetDatabase.ImportAsset` works after writing the bytes,
        /// honors the `overwrite` flag, encodes the pixel array as PNG, and triggers AssetDatabase import.
        /// </summary>
        internal static TextureCreateResult WritePngAsset(string assetPath, int width, int height, Color[] pixels, bool overwrite)
        {
            // overwrite guard — `AssetDatabase.AssetPathExists` would be ideal but is 2023+,
            // so test via File.Exists which works in 2022.3.
            string projectRoot = Directory.GetCurrentDirectory();
            string absPath = Path.Combine(projectRoot, assetPath).Replace('\\', '/');

            bool alreadyExists = File.Exists(absPath);
            if (alreadyExists && !overwrite)
            {
                return new TextureCreateResult
                {
                    Ok = false,
                    AssetPath = assetPath,
                    Width = width,
                    Height = height,
                    Error = Error.AssetAlreadyExists(assetPath),
                };
            }

            string? directory = Path.GetDirectoryName(absPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            var tex = new Texture2D(width, height, TextureFormat.RGBA32, mipChain: false);
            try
            {
                tex.SetPixels(pixels);
                tex.Apply(updateMipmaps: false, makeNoLongerReadable: false);

                byte[] png = tex.EncodeToPNG();
                if (png == null || png.Length == 0)
                {
                    return new TextureCreateResult
                    {
                        Ok = false,
                        AssetPath = assetPath,
                        Width = width,
                        Height = height,
                        Error = Error.FailedToEncodePng(assetPath),
                    };
                }

                File.WriteAllBytes(absPath, png);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(tex);
            }

            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

            return new TextureCreateResult
            {
                Ok = true,
                AssetPath = assetPath,
                Width = width,
                Height = height,
            };
        }

        public static class Error
        {
            public static string EmptyAssetPath()
                => "Asset path is empty. Sample: \"Assets/Textures/Generated.png\".";

            public static string AssetPathMustStartWithAssets(string assetPath)
                => $"Asset path must start with 'Assets/'. Path: '{assetPath}'.";

            public static string AssetPathMustEndWithPng(string assetPath)
                => $"Asset path must end with '.png'. Path: '{assetPath}'.";

            public static string AssetPathExtensionUnsupported(string assetPath)
                => $"Asset path extension is not supported. Expected one of '.png', '.jpg', '.jpeg'. Path: '{assetPath}'.";

            public static string DimensionOutOfRange(string name, int value)
                => $"'{name}' is out of range. Got {value}. Must be in [1, {MaxDimension}].";

            public static string TooManyPixels(int width, int height)
                => $"Requested texture ({width}x{height} = {(long)width * height} pixels) exceeds the budget " +
                   $"of {MaxTotalPixels} pixels. Split the texture into smaller tiles or lower the resolution.";

            public static string ColorChannelNotFinite(string fieldName)
                => $"Color channel '{fieldName}' is not a finite number (NaN or Infinity).";

            public static string ColorChannelOutOfRange(string fieldName, float value)
                => $"Color channel '{fieldName}' is out of range. Got {value}. Must be in [0, 1].";

            public static string SquareSizeOutOfRange(int squareSize)
                => $"'squareSize' is out of range. Got {squareSize}. Must be in [1, max(width,height)].";

            public static string ScaleNotPositive(float scale)
                => $"'scale' must be > 0 (got {scale}).";

            public static string ScaleNotFinite(float scale)
                => $"'scale' must be a finite number (got {scale}).";

            public static string OctavesOutOfRange(int octaves)
                => $"'octaves' is out of range. Got {octaves}. Must be in [1, 16].";

            public static string PersistenceOutOfRange(float persistence)
                => $"'persistence' is out of range. Got {persistence}. Must be in [0, 1].";

            public static string AssetAlreadyExists(string assetPath)
                => $"Asset already exists at '{assetPath}'. Pass overwrite=true to replace it.";

            public static string FailedToEncodePng(string assetPath)
                => $"Failed to encode the generated texture as PNG. Path: '{assetPath}'.";

            public static string AssetNotFound(string assetPath)
                => $"Asset not found at '{assetPath}'.";
        }
    }

    /// <summary>RGBA color where each channel is normalized in [0, 1].</summary>
    public class ColorRgba
    {
        [Description("Red 0..1")]
        public float R { get; set; } = 1f;

        [Description("Green 0..1")]
        public float G { get; set; } = 1f;

        [Description("Blue 0..1")]
        public float B { get; set; } = 1f;

        [Description("Alpha 0..1")]
        public float A { get; set; } = 1f;

        public UnityEngine.Color ToColor() => new UnityEngine.Color(R, G, B, A);

        public static ColorRgba From(UnityEngine.Color c)
            => new ColorRgba { R = c.r, G = c.g, B = c.b, A = c.a };
    }

    public enum GradientDirection
    {
        Horizontal,
        Vertical,
        Diagonal,
        Radial
    }

    /// <summary>Common result for every `texture-create-*` tool.</summary>
    public class TextureCreateResult
    {
        public bool Ok { get; set; }
        public string AssetPath { get; set; } = string.Empty;
        public int Width { get; set; }
        public int Height { get; set; }
        public string? Error { get; set; }
    }

    /// <summary>Result for `texture-read`.</summary>
    public class TextureReadResult
    {
        public bool Ok { get; set; }
        public string AssetPath { get; set; } = string.Empty;
        public int Width { get; set; }
        public int Height { get; set; }
        public string Format { get; set; } = string.Empty;
        public bool MipmapEnabled { get; set; }
        public string FilterMode { get; set; } = string.Empty;
        public string WrapMode { get; set; } = string.Empty;
        public int AnisoLevel { get; set; }
        public bool IsReadable { get; set; }
        public string TextureType { get; set; } = string.Empty;
        public string? Error { get; set; }
    }
}
