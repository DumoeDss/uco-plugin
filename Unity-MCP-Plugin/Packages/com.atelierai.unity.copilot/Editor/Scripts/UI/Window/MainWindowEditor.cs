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
using com.IvanMurzak.McpPlugin;
using R3;
using UnityEngine;
using UnityEngine.UIElements;

namespace com.AtelierAI.Unity.Copilot.Editor.UI
{
    public partial class MainWindowEditor : CopilotWindowBase
    {
        readonly CompositeDisposable _disposables = new();

        Button? _btnConnect;
        Button? _btnAuthorize;
        Action? _startAuthorizeAction;
        VisualElement? _timelinePointUnity;
        AlertPanel? _connectionAuthAlert;
        AlertPanel? _connectionConnectAlert;

        protected override string WindowTitle => "Game Developer";
        protected override string[] WindowUxmlPaths => _windowUxmlPaths;
        protected override string[] WindowUssPaths => _windowUssPaths;

        public static MainWindowEditor ShowWindow()
        {
            var window = GetWindow<MainWindowEditor>("Game Developer");
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

        internal static (bool needsAuth, bool hasToken, bool isCloud) ComputeCloudAuthState(ConnectionMode mode, string? token)
        {
            var isCloud = mode == ConnectionMode.Cloud;
            var hasToken = !string.IsNullOrEmpty(token);
            var needsAuth = isCloud && !hasToken;
            return (needsAuth, hasToken, isCloud);
        }

        private void UpdateCloudAuthState()
        {
            var (needsAuth, hasToken, isCloud) = ComputeCloudAuthState(UnityCopilotPluginEditor.ConnectionMode, UnityCopilotPluginEditor.CloudToken);

            if (_timelinePointUnity != null)
            {
                _timelinePointUnity.SetEnabled(!needsAuth);
                _timelinePointUnity.tooltip = needsAuth
                    ? "Cloud token is required. Press the Authorize button to authenticate."
                    : "";
            }
            if (_btnConnect != null)
            {
                if (needsAuth)
                {
                    _btnConnect.text = ServerButtonText_Connect;
                    _btnConnect.EnableInClassList("btn-primary", false);
                    _btnConnect.EnableInClassList("btn-secondary", true);
                }
                else if (isCloud && hasToken
                    && _btnConnect.text == ServerButtonText_Connect)
                {
                    _btnConnect.EnableInClassList("btn-primary", true);
                    _btnConnect.EnableInClassList("btn-secondary", false);
                }
            }
            if (_btnAuthorize != null)
            {
                _btnAuthorize.EnableInClassList("btn-primary", !hasToken);
            }
            _connectionAuthAlert?.SetVisible(needsAuth);

            // Show connect alert when authorized in Cloud mode but not connected and not trying
            if (_connectionConnectAlert != null)
            {
                var connectionState = UnityCopilotPluginEditor.ConnectionState.CurrentValue;
                var keepConnected = UnityCopilotPluginEditor.KeepConnected;
                var isDisconnected = connectionState == ConnectionState.Disconnected;
                var needsConnect = isCloud && hasToken && isDisconnected && !keepConnected;
                _connectionConnectAlert.SetVisible(needsConnect);
            }
        }

        private static void UnityBuildAndConnect()
        {
            UnityCopilotPluginEditor.Instance.BuildMcpPluginIfNeeded();
            UnityCopilotPluginEditor.Instance.AddUnityLogCollectorIfNeeded(() => new BufferedFileLogStorage());
            UnityCopilotPluginEditor.ConnectIfNeeded();
        }

        /// <summary>
        /// Disconnects, disposes the current MCP plugin, rebuilds it (picking up the new Host/Token
        /// from the changed ConnectionMode), and reconnects if KeepConnected is enabled.
        /// Called when switching between Local and Cloud modes.
        /// </summary>
        private static void ReconnectAfterModeSwitch()
        {
            if (UnityCopilotPluginEditor.Instance.HasMcpPluginInstance)
            {
                UnityCopilotPluginEditor.Instance.DisposeMcpPluginInstance();
            }
            UnityBuildAndConnect();
        }
    }
}