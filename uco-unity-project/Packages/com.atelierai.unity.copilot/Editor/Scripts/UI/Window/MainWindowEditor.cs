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
using com.AtelierAI.Unity.Copilot.Runtime.Utils;
using com.AtelierAI.Uco.Framework;
using R3;
using UnityEngine;
using UnityEngine.UIElements;

namespace com.AtelierAI.Unity.Copilot.Editor.UI
{
    public partial class MainWindowEditor : CopilotWindowBase
    {
        readonly CompositeDisposable _disposables = new();

        Button? _btnConnect;
        VisualElement? _timelinePointUnity;

        protected override string WindowTitle => "Unity Copilot";
        protected override string[] WindowUxmlPaths => _windowUxmlPaths;
        protected override string[] WindowUssPaths => _windowUssPaths;

        public static MainWindowEditor ShowWindow()
        {
            var window = GetWindow<MainWindowEditor>("Unity Copilot");
            window.SetupWindowWithIcon();
            window.Focus();

            return window;
        }
        public static void ShowWindowVoid() => ShowWindow();

        public void Invalidate()
        {
            CreateGUI();
        }
        void OnValidate() => UnityCopilotPluginEditor.Instance.Validate();

        private void SaveChanges(string message)
        {
            if (UnityCopilotPlugin.IsLogEnabled(LogLevel.Info))
                Debug.Log(message);

            saveChangesMessage = message;

            base.SaveChanges();
            UnityCopilotPluginEditor.Instance.Save();
        }

        private void OnChanged(UnityCopilotPlugin.UnityConnectionConfig data) => Repaint();

        protected override void OnEnable()
        {
            base.OnEnable();
            _disposables.Add(UnityCopilotPluginEditor.SubscribeOnChanged(OnChanged));
        }
        private void OnDisable()
        {
            _disposables.Clear();
            _authRejectedSubscription.Dispose();
        }

        private static void UnityBuildAndConnect()
        {
            UnityCopilotPluginEditor.Instance.BuildUcoPluginIfNeeded();
            UnityCopilotPluginEditor.Instance.AddUnityLogCollectorIfNeeded(() => new BufferedFileLogStorage());
            UnityCopilotPluginEditor.ConnectIfNeeded();
        }
    }
}