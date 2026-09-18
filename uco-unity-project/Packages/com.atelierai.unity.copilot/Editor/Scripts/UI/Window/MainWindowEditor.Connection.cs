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
using com.IvanMurzak.ReflectorNet.Utils;
using com.AtelierAI.Unity.Copilot.Editor.UI.Controls;
using com.AtelierAI.Uco.Framework;
using R3;
using UnityEngine;
using UnityEngine.UIElements;
using static com.AtelierAI.Uco.Framework.Common.Consts.Uco.Server;

namespace com.AtelierAI.Unity.Copilot.Editor.UI
{
    public partial class MainWindowEditor
    {
        private TextField? _inputFieldHost;
        private VisualElement? _connectionStatusCircle;
        private Label? _connectionStatusText;
        private readonly SerialDisposable _authRejectedSubscription = new();

        private void SetupConnectionSection(VisualElement root)
        {
            _inputFieldHost = root.Q<TextField>("InputServerURL");
            var btnConnect = root.Q<Button>("btnConnectOrDisconnect");
            _connectionStatusCircle = root.Q<VisualElement>("connectionStatusCircle");
            _connectionStatusText = root.Q<Label>("connectionStatusText");

            _btnConnect = btnConnect;
            _timelinePointUnity = root.Q<VisualElement>("TimelinePointUnity");
            if (_timelinePointUnity != null)
                _timelinePointUnity.tooltip = Tooltip_UnityTimelineLabel;
            if (_connectionStatusCircle != null)
                _connectionStatusCircle.tooltip = Tooltip_UnityTimelineLabel;
            if (_connectionStatusText != null)
                _connectionStatusText.tooltip = Tooltip_UnityTimelineLabel;

            _aiAgentLabelsContainer = root.Q<VisualElement>("aiAgentLabelsContainer");
            _aiAgentStatusCircle = root.Q<VisualElement>("aiAgentStatusCircle");

            var timelinePointAiAgent = root.Q<VisualElement>("TimelinePointAiAgent");
            if (timelinePointAiAgent != null)
                timelinePointAiAgent.tooltip = Tooltip_AiAgentTimelineLabel;

            _inputFieldHost.value = UnityCopilotPluginEditor.LocalHost;
            _inputFieldHost.RegisterCallback<FocusOutEvent>(evt =>
            {
                var newValue = _inputFieldHost.value;
                if (UnityCopilotPluginEditor.LocalHost == newValue)
                    return;

                UnityCopilotPluginEditor.LocalHost = newValue;
                SaveChanges($"[{nameof(MainWindowEditor)}] Host Changed: {newValue}");
                Invalidate();

                UnityCopilotPluginEditor.Instance.DisposeUcoPluginInstance();
                UnityBuildAndConnect();
            });

            SubscribeToConnectionState((state, keepConnected) =>
            {
                UpdateConnectionUI(state, keepConnected);
            });

            UnityCopilotPluginEditor.PluginProperty
                .WhereNotNull()
                .Subscribe(plugin =>
                {
                    _authRejectedSubscription.Disposable = plugin.OnAuthorizationRejected
                        .ObserveOnCurrentSynchronizationContext()
                        .Subscribe(_ => OnAuthorizationRejected());
                })
                .AddTo(_disposables);

            btnConnect.RegisterCallback<ClickEvent>(evt => HandleConnectButton(btnConnect.text));
        }

        private void OnAuthorizationRejected()
        {
            Debug.LogWarning("[Unity Copilot] The server rejected the authorization token. " +
                "The token has been cleared. Update the token (or regenerate it) and reconnect.");

            UnityCopilotPluginEditor.LocalToken = null;
            UnityCopilotPluginEditor.Instance.Save();
            RefreshConnectionUI();
        }

        private void UpdateConnectionUI(ConnectionState state, bool keepConnected)
        {
            if (_inputFieldHost == null || _connectionStatusText == null
                || _btnConnect == null || _connectionStatusCircle == null)
                return;

            UpdateHostFieldState(_inputFieldHost, keepConnected, state);
            _connectionStatusText.text = "Unity: " + GetConnectionStatusText(state, keepConnected);
            _btnConnect.text = GetButtonText(state, keepConnected);
            var isConnect = _btnConnect.text == ServerButtonText_Connect;
            _btnConnect.EnableInClassList("btn-primary", isConnect);
            _btnConnect.EnableInClassList("btn-secondary", !isConnect);
            SetStatusIndicator(_connectionStatusCircle, GetConnectionStatusClass(state, keepConnected));

            if (!(state == ConnectionState.Connected && keepConnected))
                SetAiAgentStatus(false);
        }

        /// <summary>
        /// Reads the current connection state and refreshes the Unity connection row UI.
        /// Call this whenever the UI might be stale.
        /// </summary>
        private void RefreshConnectionUI()
        {
            var state = UnityCopilotPluginEditor.ConnectionState.CurrentValue;
            var keepConnected = UnityCopilotPluginEditor.KeepConnected;
            UpdateConnectionUI(state, keepConnected);
        }

        /// <summary>
        /// Schedules a delayed <see cref="RefreshConnectionUI"/> to catch state changes
        /// that arrive after a reconnect (e.g. async handshake).
        /// </summary>
        private void ScheduleConnectionUIRefresh()
        {
            rootVisualElement?.schedule.Execute(() => RefreshConnectionUI()).ExecuteLater(500);
            rootVisualElement?.schedule.Execute(() => RefreshConnectionUI()).ExecuteLater(2000);
        }

        internal static bool IsHostFieldReadOnly(bool keepConnected, ConnectionState state) =>
            keepConnected || state != ConnectionState.Disconnected;

        private static void UpdateHostFieldState(TextField field, bool keepConnected, ConnectionState state)
        {
            var isReadOnly = IsHostFieldReadOnly(keepConnected, state);
            field.isReadOnly = isReadOnly;
            var defaultUrl = $"http://localhost:{UnityCopilotPlugin.GeneratePortFromDirectory()}";
            field.tooltip = keepConnected
                ? "Editable only when Unity disconnected from the server."
                : $"Usually the server is hosted locally at {defaultUrl}. Feel free to connect to a remote server if needed. The connection is established using WebSocket.";

            field.EnableInClassList("disabled-text-field", isReadOnly);
            field.EnableInClassList("enabled-text-field", !isReadOnly);
        }

        private void HandleConnectButton(string buttonText)
        {
            if (buttonText.Equals(ServerButtonText_Connect, StringComparison.OrdinalIgnoreCase))
            {
                ConnectToServer();
            }
            else
            {
                UnityCopilotPluginEditor.KeepConnected = false;
                UnityCopilotPluginEditor.Instance.Save();
                if (UnityCopilotPluginEditor.Instance.HasUcoPluginInstance)
                    _ = UnityCopilotPluginEditor.Instance.Disconnect();
            }
            ScheduleConnectionUIRefresh();
        }

        /// <summary>
        /// Initiates connection to the server. Called by the Connect button.
        /// </summary>
        private static void ConnectToServer()
        {
            UnityCopilotPluginEditor.KeepConnected = true;
            UnityCopilotPluginEditor.Instance.Save();
            UnityBuildAndConnect();
        }
    }
}
