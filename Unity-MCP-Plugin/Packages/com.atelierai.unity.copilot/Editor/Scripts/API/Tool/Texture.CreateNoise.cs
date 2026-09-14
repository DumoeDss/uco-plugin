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
using com.IvanMurzak.McpPlugin;
using com.IvanMurzak.ReflectorNet.Utils;
using UnityEngine;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    public partial class Tool_Texture
    {
        public const string TextureCreateNoiseToolId = "texture-create-noise";

        [McpPluginTool
        (
            TextureCreateNoiseToolId,
            Title = "Texture / Create Perlin Noise"
        )]
        [McpPluginSkillDescription("Generate a Texture2D filled with fractal Perlin noise (octave-summed fBm using " +
            "`UnityEngine.Mathf.PerlinNoise`) and save it as a PNG asset under 'Assets/'. Output is grayscale by " +
            "default, or tinted by an RGB color when `grayscale=false`.")]
        [McpPluginSkillBody("Generate a Perlin / fBm noise Texture2D and import it into the Unity project.\n\n" +
            "## Inputs\n\n" +
            "- `path` — must start with `Assets/` and end with `.png`.\n" +
            "- `width`, `height` — pixel dimensions in [1, 4096]; total pixel count capped at 16,777,216.\n" +
            "- `scale` — base frequency in tiles across the texture. Higher = finer detail. Must be > 0.\n" +
            "- `octaves` — number of fBm octaves in [1, 16]. Each octave doubles the frequency and multiplies the " +
            "amplitude by `persistence`.\n" +
            "- `persistence` — amplitude falloff per octave in [0, 1]. `0.5` is a typical fBm value.\n" +
            "- `seed` — integer offset added to the sample coordinates. Use to reproduce or vary a noise field.\n" +
            "- `tint` — RGBA color in [0, 1]. Default opaque white. Multiplied with the noise value per channel.\n" +
            "- `grayscale` — when `true` (default) all three RGB channels carry the same noise value; the `tint` " +
            "alpha is still applied. When `false` the RGB output is `tint.rgb * sample`.\n" +
            "- `overwrite` — when `false` (default) and the asset already exists, returns `Ok=false`.\n\n" +
            "## Algorithm\n\n" +
            "For each pixel:\n" +
            "```\n" +
            "sample = 0; amplitude = 1; frequency = scale; maxValue = 0\n" +
            "for o in 0..octaves:\n" +
            "  nx = (x + seed) * frequency / width\n" +
            "  ny = (y + seed) * frequency / height\n" +
            "  sample    += Mathf.PerlinNoise(nx, ny) * amplitude\n" +
            "  maxValue  += amplitude\n" +
            "  amplitude *= persistence\n" +
            "  frequency *= 2\n" +
            "sample /= maxValue   // normalize to ~[0, 1]\n" +
            "```\n\n" +
            "`Mathf.PerlinNoise` is Unity's built-in gradient noise — no third-party dependency.")]
        [Description("Generate a Texture2D filled with Perlin / fBm noise and save it as a PNG asset under 'Assets/'.")]
        public TextureCreateResult CreateNoise
        (
            [Description("Asset path under 'Assets/'. Must end with '.png'.")]
            string path,
            [Description("Width in pixels. 1..4096.")]
            int width,
            [Description("Height in pixels. 1..4096.")]
            int height,
            [Description("Base frequency in tiles across the texture. Higher = finer detail. > 0.")]
            float scale = 8f,
            [Description("Number of fBm octaves in [1, 16]. Default 1 (single octave Perlin).")]
            int octaves = 1,
            [Description("Amplitude falloff per octave in [0, 1]. Default 0.5.")]
            float persistence = 0.5f,
            [Description("Integer offset applied to sample coordinates. Default 0.")]
            int seed = 0,
            [Description("RGBA tint applied multiplicatively. Default opaque white.")]
            ColorRgba? tint = null,
            [Description("When true (default) the output RGB channels carry the same noise value. When false they are tint.rgb * sample.")]
            bool grayscale = true,
            [Description("Force overwrite if asset already exists. Default false.")]
            bool overwrite = false
        )
        {
            return MainThread.Instance.Run(() =>
            {
                ValidateAssetPathPng(path, nameof(path));
                ValidateDimensions(width, height);

                if (float.IsNaN(scale) || float.IsInfinity(scale))
                    throw new ArgumentException(Error.ScaleNotFinite(scale), nameof(scale));
                if (scale <= 0f)
                    throw new ArgumentException(Error.ScaleNotPositive(scale), nameof(scale));

                if (octaves < 1 || octaves > 16)
                    throw new ArgumentException(Error.OctavesOutOfRange(octaves), nameof(octaves));

                if (float.IsNaN(persistence) || float.IsInfinity(persistence) || persistence < 0f || persistence > 1f)
                    throw new ArgumentException(Error.PersistenceOutOfRange(persistence), nameof(persistence));

                var effectiveTint = tint ?? new ColorRgba();
                ValidateColor(effectiveTint, nameof(tint));
                Color tintColor = effectiveTint.ToColor();

                var pixels = new Color[width * height];
                float invWidth = 1f / width;
                float invHeight = 1f / height;

                for (int y = 0; y < height; y++)
                {
                    int row = y * width;
                    for (int x = 0; x < width; x++)
                    {
                        float sample = 0f;
                        float frequency = scale;
                        float amplitude = 1f;
                        float maxValue = 0f;

                        for (int o = 0; o < octaves; o++)
                        {
                            float nx = (x + seed) * frequency * invWidth;
                            float ny = (y + seed) * frequency * invHeight;
                            sample += Mathf.PerlinNoise(nx, ny) * amplitude;
                            maxValue += amplitude;
                            amplitude *= persistence;
                            frequency *= 2f;
                        }

                        if (maxValue > 0f)
                            sample /= maxValue;
                        // Mathf.PerlinNoise is documented to return values in [0,1] but in practice it can
                        // exceed that range slightly — clamp before encoding.
                        if (sample < 0f) sample = 0f;
                        else if (sample > 1f) sample = 1f;

                        Color c;
                        if (grayscale)
                        {
                            c = new Color(sample, sample, sample, tintColor.a);
                        }
                        else
                        {
                            c = new Color(tintColor.r * sample, tintColor.g * sample, tintColor.b * sample, tintColor.a);
                        }
                        pixels[row + x] = c;
                    }
                }

                return WritePngAsset(path, width, height, pixels, overwrite);
            });
        }
    }
}
