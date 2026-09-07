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

namespace com.AtelierAI.Unity.Copilot.Editor.UI
{
    [InitializeOnLoad]
    static class MainWindowInitializer
    {
        static PlayerPrefsBool isInitialized = new PlayerPrefsBool("Unity-MCP.MainWindow.Initialized");

        static MainWindowInitializer()
        {
            if (!UnityProcessGuard.ShouldInitialize(AssetDatabase.IsAssetImportWorkerProcess()))
                return;

            if (isInitialized.Value)
                return;

            EditorApplication.update += PerformInitialization;
        }

        static void PerformInitialization()
        {
            EditorApplication.update -= PerformInitialization;

            if (isInitialized.Value)
                return;

            // Perform initialization
            UnityCopilotPluginEditor.InitSingletonIfNeeded();
            MainWindowEditor.ShowWindow();

            isInitialized.Value = true;
        }
    }
}
