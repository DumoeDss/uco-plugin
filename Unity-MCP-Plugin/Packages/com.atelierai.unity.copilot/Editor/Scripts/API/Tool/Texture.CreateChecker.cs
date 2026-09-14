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
        public const string TextureCreateCheckerToolId = "texture-create-checker";

        [McpPluginTool
        (
            TextureCreateCheckerToolId,
            Title = "Texture / Create Checker Pattern"
        )]
        [McpPluginSkillDescription("Generate a Texture2D with a two-color checkerboard pattern and save it as a PNG " +
            "asset under 'Assets/'. The cell size is `squareSize` pixels.")]
        [McpPluginSkillBody("Generate a checkerboard Texture2D and import it into the Unity project.\n\n" +
            "## Inputs\n\n" +
            "- `path` — must start with `Assets/` and end with `.png`.\n" +
            "- `width`, `height` — pixel dimensions in [1, 4096]; total pixel count capped at 16,777,216.\n" +
            "- `squareSize` — side length of one checker cell in pixels. Must be ≥ 1.\n" +
            "- `color1` — color at cell `(0,0)` (RGBA in [0, 1]).\n" +
            "- `color2` — color of the alternating cells (RGBA in [0, 1]).\n" +
            "- `overwrite` — when `false` (default) and the asset already exists, returns `Ok=false`.\n\n" +
            "## Algorithm\n\n" +
            "For each pixel `(x, y)` the cell index is `(cx, cy) = (x / squareSize, y / squareSize)`. " +
            "When `(cx + cy)` is even the pixel uses `color1`, otherwise `color2`. " +
            "This is a classic two-color checker without anti-aliasing — useful for UV grids and debug textures.")]
        [Description("Generate a Texture2D with a two-color checkerboard pattern and save it as a PNG asset under 'Assets/'.")]
        public TextureCreateResult CreateChecker
        (
            [Description("Asset path under 'Assets/'. Must end with '.png'.")]
            string path,
            [Description("Width in pixels. 1..4096.")]
            int width,
            [Description("Height in pixels. 1..4096.")]
            int height,
            [Description("Side length of a single checker cell in pixels. Must be >= 1.")]
            int squareSize = 8,
            [Description("Color of the (0,0) cell. RGBA channels in 0..1. Default opaque white.")]
            ColorRgba? color1 = null,
            [Description("Color of the alternating cells. RGBA channels in 0..1. Default opaque black.")]
            ColorRgba? color2 = null,
            [Description("Force overwrite if asset already exists. Default false.")]
            bool overwrite = false
        )
        {
            return MainThread.Instance.Run(() =>
            {
                ValidateAssetPathPng(path, nameof(path));
                ValidateDimensions(width, height);

                if (squareSize < 1 || squareSize > Mathf.Max(width, height))
                    throw new ArgumentException(Error.SquareSizeOutOfRange(squareSize), nameof(squareSize));

                var c1 = color1 ?? new ColorRgba { R = 1f, G = 1f, B = 1f, A = 1f };
                var c2 = color2 ?? new ColorRgba { R = 0f, G = 0f, B = 0f, A = 1f };
                ValidateColor(c1, nameof(color1));
                ValidateColor(c2, nameof(color2));

                Color a = c1.ToColor();
                Color b = c2.ToColor();
                var pixels = new Color[width * height];

                for (int y = 0; y < height; y++)
                {
                    int row = y * width;
                    int cy = y / squareSize;
                    for (int x = 0; x < width; x++)
                    {
                        int cx = x / squareSize;
                        pixels[row + x] = ((cx + cy) & 1) == 0 ? a : b;
                    }
                }

                return WritePngAsset(path, width, height, pixels, overwrite);
            });
        }
    }
}
