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
using com.AtelierAI.Unity.Copilot.Editor.Services;
using com.AtelierAI.Unity.Copilot.Editor.UI.Controls;
using com.IvanMurzak.McpPlugin;
using R3;
using UnityEngine;
using UnityEngine.UIElements;
using static com.IvanMurzak.McpPlugin.Common.Consts.MCP.Server;

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

                UnityCopilotPluginEditor.Instance.DisposeMcpPluginInstance();
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
            if (UnityCopilotPluginEditor.ConnectionMode != ConnectionMode.Cloud)
                return;

            Debug.LogWarning("[AI Game Developer] The server rejected the authorization token. " +
                "The token has been cleared. Please click 'Authorize' to obtain a new token.");

            UnityCopilotPluginEditor.CloudToken = null;
            UnityCopilotPluginEditor.Instance.Save();

            UpdateCloudAuthState();
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

            UpdateCloudAuthState();
        }

        /// <summary>
        /// Reads the current connection state and refreshes the Unity connection row UI.
        /// Call this whenever the UI might be stale (e.g. after mode switch).
        /// </summary>
        private void RefreshConnectionUI()
        {
            var state = UnityCopilotPluginEditor.ConnectionState.CurrentValue;
            var keepConnected = UnityCopilotPluginEditor.KeepConnected;
            UpdateConnectionUI(state, keepConnected);
        }

        /// <summary>
        /// Schedules a delayed <see cref="RefreshConnectionUI"/> to catch state changes
        /// that arrive after a mode switch or reconnect (e.g. async SignalR handshake).
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
                if (UnityCopilotPluginEditor.Instance.HasMcpPluginInstance)
                    _ = UnityCopilotPluginEditor.Instance.Disconnect();
            }
            ScheduleConnectionUIRefresh();
        }

        /// <summary>
        /// Initiates connection to the server. Called by both the Connect button and the
        /// connection alert panel's Connect button.
        /// </summary>
        private static void ConnectToServer()
        {
            UnityCopilotPluginEditor.KeepConnected = true;
            UnityCopilotPluginEditor.Instance.Save();
            UnityBuildAndConnect();
        }

        private void SetupConnectionModeToggle(VisualElement root)
        {
            var container = root.Q<VisualElement>("segmentConnectionMode");
            if (container == null) return;

            var control = new SegmentedControl("Custom", "Cloud");
            control.SetTooltips(
                "Connect to your own server. The plugin starts a local server automatically and manages its lifecycle. Use this when you want full control over the server configuration, port, and authorization settings.",
                "Connect to a remote server hosted in the cloud (e.g. ai-game.dev). No local server is started — the plugin connects directly to a built-in cloud endpoint (Cloud URL is predefined and not configurable). Requires authorization via device code flow.");
            container.Add(control);

            var inputServerUrl = root.Q<TextField>("InputServerURL");
            var mcpServerPoint = root.Q<VisualElement>("TimelinePointMcpServer");
            var cloudAuthSection = root.Q<VisualElement>("cloudAuthSection");

            void UpdateModeVisibility(ConnectionMode mode)
            {
                var isCustom = mode == ConnectionMode.Custom;
                if (inputServerUrl != null) inputServerUrl.style.display = isCustom ? DisplayStyle.Flex : DisplayStyle.None;
                if (mcpServerPoint != null) mcpServerPoint.style.display = isCustom ? DisplayStyle.Flex : DisplayStyle.None;
                if (cloudAuthSection != null) cloudAuthSection.style.display = isCustom ? DisplayStyle.None : DisplayStyle.Flex;
            }

            var currentMode = UnityCopilotPluginEditor.ConnectionMode;
            control.SetValueWithoutNotify(currentMode == ConnectionMode.Custom ? 0 : 1);
            UpdateModeVisibility(currentMode);

            control.RegisterCallback<ChangeEvent<int>>(evt =>
            {
                if (evt.newValue == 0)
                {
                    UnityCopilotPluginEditor.ConnectionMode = ConnectionMode.Custom;
                    UnityCopilotPluginEditor.Instance.Save();
                    UpdateModeVisibility(ConnectionMode.Custom);
                    UpdateCloudAuthState();

                    // Start local server if configured and reconnect to it
                    CopilotServerManager.StartServerIfNeeded();
                    ReconnectAfterModeSwitch();
                    ScheduleConnectionUIRefresh();
                }
                else
                {
                    UnityCopilotPluginEditor.ConnectionMode = ConnectionMode.Cloud;

                    // Cloud requires authorization
                    UnityCopilotPluginEditor.AuthOption = AuthOption.required;

                    UnityCopilotPluginEditor.Instance.Save();
                    UpdateModeVisibility(ConnectionMode.Cloud);
                    UpdateCloudAuthState();

                    // Stop local server — not needed in Cloud mode
                    if (CopilotServerManager.IsRunning || CopilotServerManager.IsStarting)
                        CopilotServerManager.StopServer();

                    // Reconnect to cloud server (only if authorized)
                    if (!string.IsNullOrEmpty(UnityCopilotPluginEditor.CloudToken))
                        ReconnectAfterModeSwitch();
                    ScheduleConnectionUIRefresh();
                }
            });
        }

        internal static bool IsAuthFlowRunning(DeviceAuthFlowState state) =>
            state == DeviceAuthFlowState.Initiating
            || state == DeviceAuthFlowState.WaitingForUser
            || state == DeviceAuthFlowState.Polling;

        internal static string GetAuthFlowStatusMessage(DeviceAuthFlowState state, string? userCode, string? errorMessage) => state switch
        {
            DeviceAuthFlowState.Initiating => "Initiating...",
            DeviceAuthFlowState.WaitingForUser => $"Code: {userCode} — Authorize in browser",
            DeviceAuthFlowState.Polling => $"Code: {userCode} — Waiting for authorization...",
            DeviceAuthFlowState.Authorized => "Authorized!",
            DeviceAuthFlowState.Failed => $"Failed: {errorMessage}",
            DeviceAuthFlowState.Expired => "Expired — try again",
            DeviceAuthFlowState.Cancelled => "Cancelled",
            _ => ""
        };

        private void SetupCloudAuthSection(VisualElement root)
        {
            var inputCloudToken = root.Q<TextField>("inputCloudToken");
            var btnRevoke = root.Q<Button>("btnCloudRevoke");
            var btnAuthorize = root.Q<Button>("btnCloudAuthorize");
            var statusLabel = root.Q<Label>("labelCloudAuthStatus");
            if (inputCloudToken == null || btnAuthorize == null) return;

            _btnAuthorize = btnAuthorize;

            inputCloudToken.isPasswordField = true;
            inputCloudToken.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode == KeyCode.C && (evt.ctrlKey || evt.commandKey))
                {
                    GUIUtility.systemCopyBuffer = inputCloudToken.value;
                    evt.StopPropagation();
                }
            });

            const string tokenPlaceholder = "Token — press Authorize";
            void SetTokenValue(string? token)
            {
                var isEmpty = string.IsNullOrEmpty(token);
                inputCloudToken.value = isEmpty ? tokenPlaceholder : token!;
                inputCloudToken.EnableInClassList("token-placeholder", isEmpty);
            }

            SetTokenValue(UnityCopilotPluginEditor.CloudToken);
            UpdateCloudAuthState();

            void UpdateRevokeButtonVisibility()
            {
                if (btnRevoke != null)
                    btnRevoke.style.display = string.IsNullOrEmpty(UnityCopilotPluginEditor.CloudToken)
                        ? DisplayStyle.None
                        : DisplayStyle.Flex;
            }
            UpdateRevokeButtonVisibility();

            btnRevoke?.RegisterCallback<ClickEvent>(evt =>
            {
                UnityCopilotPluginEditor.CloudToken = null;
                UnityCopilotPluginEditor.Instance.Save();
                SetTokenValue(null);
                UpdateRevokeButtonVisibility();

                if (statusLabel != null)
                {
                    statusLabel.text = "Token revoked.";
                    statusLabel.style.display = DisplayStyle.Flex;
                }

                UpdateCloudAuthState();

                // Disconnect if currently in Cloud mode
                if (UnityCopilotPluginEditor.ConnectionMode == ConnectionMode.Cloud
                    && UnityCopilotPluginEditor.Instance.HasMcpPluginInstance)
                    _ = UnityCopilotPluginEditor.Instance.Disconnect();
            });

            _startAuthorizeAction = async () =>
            {
                // If currently running, cancel
                if (_deviceAuthFlow != null && IsAuthFlowRunning(_deviceAuthFlow.State))
                {
                    _deviceAuthFlow.Cancel();
                    return;
                }

                _deviceAuthFlow?.Cancel();
                _deviceAuthFlow = new DeviceAuthFlow();
                var capturedFlow = _deviceAuthFlow; // Capture to avoid stale field reference in async callbacks

                capturedFlow.OnStateChanged += state =>
                {
                    // Use RunAsync (EditorApplication.update-based) instead of delayCall so that
                    // the UI updates even when the Unity Editor window is not focused — delayCall
                    // is throttled/paused when Unity loses application focus.
                    MainThread.Instance.RunAsync(() =>
                    {
                        // Ignore stale events from a previous auth flow
                        if (_deviceAuthFlow != capturedFlow) return;

                        if (statusLabel != null)
                        {
                            statusLabel.text = GetAuthFlowStatusMessage(state, capturedFlow.UserCode, capturedFlow.ErrorMessage);
                            statusLabel.style.display = string.IsNullOrEmpty(statusLabel.text)
                                ? DisplayStyle.None
                                : DisplayStyle.Flex;
                        }
                        if (state == DeviceAuthFlowState.Authorized && inputCloudToken != null)
                        {
                            SetTokenValue(UnityCopilotPluginEditor.CloudToken);
                            UpdateRevokeButtonVisibility();
                            UpdateCloudAuthState();
                        }
                        if (state == DeviceAuthFlowState.Authorized)
                        {
                            // Reconnect to cloud server with the new token (only if still in Cloud mode)
                            if (UnityCopilotPluginEditor.ConnectionMode == ConnectionMode.Cloud)
                                ReconnectAfterModeSwitch();
                        }
                        if (btnAuthorize != null)
                        {
                            btnAuthorize.text = IsAuthFlowRunning(state) ? "Cancel" : "Authorize";
                        }
                        Repaint();
                    });
                };

                await capturedFlow.StartAsync(UnityCopilotPlugin.UnityConnectionConfig.CloudServerBaseUrl, "Unity Editor");
            };

            btnAuthorize.RegisterCallback<ClickEvent>(_ => _startAuthorizeAction?.Invoke());
        }

        private void SetupConnectionAlerts(VisualElement root)
        {
            var container = root.Q<VisualElement>("connectionAlertContainer");
            if (container == null) return;

            // Auth alert — shown when Cloud mode is active but no token
            _connectionAuthAlert = new AlertPanel(
                "Authorization Required",
                "Cloud mode requires authentication to connect. Press the button below to authorize your device."
            );
            _connectionAuthAlert.SetButton("Authorize", () => _startAuthorizeAction?.Invoke());
            container.Add(_connectionAuthAlert.Root);

            // Connect alert — shown when authorized but Unity is not connected
            _connectionConnectAlert = new AlertPanel(
                "Connection Required",
                "Cloud authorization is complete but Unity is not connected to the server."
            );
            _connectionConnectAlert.SetButton("Connect", ConnectToServer);
            container.Add(_connectionConnectAlert.Root);

            // Initial visibility
            UpdateCloudAuthState();
        }
    }
}
