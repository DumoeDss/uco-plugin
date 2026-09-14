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
using UnityEditor;
using UnityEngine;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    public partial class Tool_Texture
    {
        public const string TextureReadToolId = "texture-read";

        [UcoTool
        (
            TextureReadToolId,
            Title = "Texture / Read Metadata",
            ReadOnlyHint = true,
            IdempotentHint = true
        )]
        [UcoSkillDescription("Read metadata of an existing texture asset (`.png` / `.jpg` / `.jpeg`) under " +
            "'Assets/' — dimensions, format, mipmap state, filter / wrap mode, anisotropy, readability and the " +
            "TextureImporter `textureType`. Does not mutate the asset.")]
        [UcoSkillBody("Inspect a texture asset and return its runtime + import properties.\n\n" +
            "## Inputs\n\n" +
            "- `path` — must start with `Assets/` and end with `.png`, `.jpg`, or `.jpeg`.\n\n" +
            "## Returns\n\n" +
            "- `Ok` — `true` when the asset was found and loaded.\n" +
            "- `Width`, `Height` — pixel dimensions.\n" +
            "- `Format` — `Texture2D.format` (e.g. `RGBA32`, `DXT5`, `BC7`).\n" +
            "- `MipmapEnabled` — whether the loaded texture has mip levels > 1.\n" +
            "- `FilterMode`, `WrapMode`, `AnisoLevel` — runtime sampler state.\n" +
            "- `IsReadable` — whether `GetPixels` is allowed (set in TextureImporter).\n" +
            "- `TextureType` — TextureImporter `textureType` (Default, Sprite, NormalMap, etc.).\n\n" +
            "Throws `ArgumentException` on malformed paths. Returns `Ok=false` with `Error` set when the asset cannot be loaded.")]
        [Description("Read metadata of an existing texture asset (.png / .jpg / .jpeg) under 'Assets/'.")]
        public TextureReadResult Read
        (
            [Description("Asset path under 'Assets/'. Must end with '.png', '.jpg', or '.jpeg'.")]
            string path
        )
        {
            return MainThread.Instance.Run(() =>
            {
                if (string.IsNullOrEmpty(path))
                    throw new ArgumentException(Error.EmptyAssetPath(), nameof(path));
                if (!path.StartsWith("Assets/", StringComparison.Ordinal))
                    throw new ArgumentException(Error.AssetPathMustStartWithAssets(path), nameof(path));

                bool extOk =
                    path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase);
                if (!extOk)
                    throw new ArgumentException(Error.AssetPathExtensionUnsupported(path), nameof(path));

                var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                if (tex == null)
                {
                    return new TextureReadResult
                    {
                        Ok = false,
                        AssetPath = path,
                        Error = Error.AssetNotFound(path),
                    };
                }

                string textureType = "Default";
                bool isReadable = tex.isReadable;
                var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                if (importer != null)
                {
                    textureType = importer.textureType.ToString();
                    isReadable = importer.isReadable;
                }

                return new TextureReadResult
                {
                    Ok = true,
                    AssetPath = path,
                    Width = tex.width,
                    Height = tex.height,
                    Format = tex.format.ToString(),
                    MipmapEnabled = tex.mipmapCount > 1,
                    FilterMode = tex.filterMode.ToString(),
                    WrapMode = tex.wrapMode.ToString(),
                    AnisoLevel = tex.anisoLevel,
                    IsReadable = isReadable,
                    TextureType = textureType,
                };
            });
        }
    }
}
