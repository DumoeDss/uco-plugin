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
using com.AtelierAI.Uco.Framework;
using com.IvanMurzak.ReflectorNet.Utils;
using UnityEngine;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    public partial class Tool_Texture
    {
        public const string TextureCreateGradientToolId = "texture-create-gradient";

        [UcoTool
        (
            TextureCreateGradientToolId,
            Title = "Texture / Create Gradient"
        )]
        [UcoSkillDescription("Generate a Texture2D containing a linear-interpolated gradient from " +
            "`startColor` to `endColor` along the chosen `direction` (Horizontal/Vertical/Diagonal/Radial), " +
            "and save it as a PNG asset under 'Assets/'.")]
        [UcoSkillBody("Generate a gradient Texture2D and import it into the Unity project.\n\n" +
            "## Inputs\n\n" +
            "- `path` — must start with `Assets/` and end with `.png`.\n" +
            "- `width`, `height` — pixel dimensions in [1, 4096]; total pixel count capped at 16,777,216.\n" +
            "- `startColor`, `endColor` — RGBA in [0, 1] per channel.\n" +
            "- `direction` — one of `Horizontal` (left→right), `Vertical` (bottom→top), `Diagonal` " +
            "(bottom-left → top-right), or `Radial` (center → corners).\n" +
            "- `overwrite` — when `false` (default) and the asset already exists, returns `Ok=false`.\n\n" +
            "## Behavior\n\n" +
            "Interpolation parameter `t` is computed from pixel coordinates:\n" +
            "- Horizontal: `t = x / (width - 1)`\n" +
            "- Vertical: `t = y / (height - 1)`\n" +
            "- Diagonal: `t = (x + y) / (width + height - 2)`\n" +
            "- Radial: `t = sqrt(dx^2 + dy^2) / maxRadius`, with center at `(width/2, height/2)`.\n\n" +
            "Each pixel is `Color.Lerp(startColor, endColor, t)`.")]
        [Description("Generate a Texture2D containing a linear gradient and save it as a PNG asset under 'Assets/'.")]
        public TextureCreateResult CreateGradient
        (
            [Description("Asset path under 'Assets/'. Must end with '.png'.")]
            string path,
            [Description("Width in pixels. 1..4096.")]
            int width,
            [Description("Height in pixels. 1..4096.")]
            int height,
            [Description("Color at t=0 (left/bottom/center). RGBA channels in 0..1.")]
            ColorRgba startColor,
            [Description("Color at t=1 (right/top/corner). RGBA channels in 0..1.")]
            ColorRgba endColor,
            [Description("Gradient sweep direction: Horizontal, Vertical, Diagonal, or Radial.")]
            GradientDirection direction = GradientDirection.Horizontal,
            [Description("Force overwrite if asset already exists. Default false.")]
            bool overwrite = false
        )
        {
            return MainThread.Instance.Run(() =>
            {
                ValidateAssetPathPng(path, nameof(path));
                ValidateDimensions(width, height);

                if (startColor == null)
                    throw new ArgumentNullException(nameof(startColor));
                if (endColor == null)
                    throw new ArgumentNullException(nameof(endColor));

                ValidateColor(startColor, nameof(startColor));
                ValidateColor(endColor, nameof(endColor));

                Color a = startColor.ToColor();
                Color b = endColor.ToColor();

                var pixels = new Color[width * height];

                // Use float math; clamp `t` to [0,1] to be safe against single-pixel rows/cols.
                float maxX = Mathf.Max(1, width - 1);
                float maxY = Mathf.Max(1, height - 1);
                float maxXY = Mathf.Max(1, (width - 1) + (height - 1));

                // Radial uses center → farthest corner as the unit interval.
                float cx = (width - 1) * 0.5f;
                float cy = (height - 1) * 0.5f;
                float maxRadius = Mathf.Max(0.0001f, Mathf.Sqrt(cx * cx + cy * cy));

                for (int y = 0; y < height; y++)
                {
                    int row = y * width;
                    for (int x = 0; x < width; x++)
                    {
                        float t;
                        switch (direction)
                        {
                            case GradientDirection.Vertical:
                                t = y / maxY;
                                break;
                            case GradientDirection.Diagonal:
                                t = (x + y) / maxXY;
                                break;
                            case GradientDirection.Radial:
                            {
                                float dx = x - cx;
                                float dy = y - cy;
                                t = Mathf.Sqrt(dx * dx + dy * dy) / maxRadius;
                                break;
                            }
                            case GradientDirection.Horizontal:
                            default:
                                t = x / maxX;
                                break;
                        }
                        if (t < 0f) t = 0f;
                        else if (t > 1f) t = 1f;

                        pixels[row + x] = Color.Lerp(a, b, t);
                    }
                }

                return WritePngAsset(path, width, height, pixels, overwrite);
            });
        }
    }
}
