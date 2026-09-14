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
using System.ComponentModel;
using com.AtelierAI.Uco.Framework;
using com.IvanMurzak.ReflectorNet.Utils;
using UnityEngine;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    public partial class Tool_Texture
    {
        public const string TextureCreateSolidToolId = "texture-create-solid";

        [UcoTool
        (
            TextureCreateSolidToolId,
            Title = "Texture / Create Solid Color"
        )]
        [UcoSkillDescription("Generate a Texture2D filled with a single RGBA color and save it as a PNG asset " +
            "under 'Assets/'. Width and height must be in [1, 4096], total pixels capped at 16M.")]
        [UcoSkillBody("Generate a solid-color Texture2D, encode it as PNG, and import it into the Unity project.\n\n" +
            "## Inputs\n\n" +
            "- `path` — must start with `Assets/` and end with `.png`. Intermediate folders are created automatically.\n" +
            "- `width`, `height` — pixel dimensions in [1, 4096]; total pixel count capped at 16,777,216.\n" +
            "- `color` — RGBA in [0, 1] per channel. Defaults to opaque white when omitted.\n" +
            "- `overwrite` — when `false` (default) and the asset already exists, returns `Ok=false` with `Error` set " +
            "instead of replacing the file.\n\n" +
            "## Behavior\n\n" +
            "Throws `ArgumentException` on malformed paths, out-of-range dimensions, non-finite color channels, or " +
            "if the requested pixel budget is exceeded. On success, refreshes the AssetDatabase synchronously and " +
            "returns `Ok=true` with the imported asset path.")]
        [Description("Generate a Texture2D filled with a single RGBA color and save it as a PNG asset under 'Assets/'.")]
        public TextureCreateResult CreateSolid
        (
            [Description("Asset path under 'Assets/'. Must end with '.png'.")]
            string path,
            [Description("Width in pixels. 1..4096.")]
            int width,
            [Description("Height in pixels. 1..4096.")]
            int height,
            [Description("RGBA color, each channel in 0..1. Default (1,1,1,1) = opaque white.")]
            ColorRgba? color = null,
            [Description("Force overwrite if asset already exists. Default false.")]
            bool overwrite = false
        )
        {
            return MainThread.Instance.Run(() =>
            {
                ValidateAssetPathPng(path, nameof(path));
                ValidateDimensions(width, height);

                var effective = color ?? new ColorRgba();
                ValidateColor(effective, nameof(color));

                Color fill = effective.ToColor();
                var pixels = new Color[width * height];
                for (int i = 0; i < pixels.Length; i++)
                    pixels[i] = fill;

                return WritePngAsset(path, width, height, pixels, overwrite);
            });
        }
    }
}
