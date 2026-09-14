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
using com.AtelierAI.Unity.Copilot.Editor.Utils;
using UnityEditor;
using UnityEditor.Toolbars;
using UnityEngine;
using UnityEngine.UIElements;

namespace com.AtelierAI.Unity.Copilot.Editor.UI
{
    [EditorToolbarElement(Id, typeof(SceneView))]
    public class OpenWindowButton : EditorToolbarButton
    {
        public const string Id = "ai-game-developer-toolbar/open-window";

        public OpenWindowButton()
        {
            text = "Unity Co-Pilot";
            icon = EditorAssetLoader.LoadAssetAtPath<Texture2D>(EditorAssetLoader.PackageLogoIcon);
            tooltip = "Open Unity Co-Pilot window";
            clicked += MainWindowEditor.ShowWindowVoid;
            RegisterCallback<DetachFromPanelEvent>(OnDetachFromPanel);
        }

        private void OnDetachFromPanel(DetachFromPanelEvent evt)
        {
            clicked -= MainWindowEditor.ShowWindowVoid;
        }
    }
}
