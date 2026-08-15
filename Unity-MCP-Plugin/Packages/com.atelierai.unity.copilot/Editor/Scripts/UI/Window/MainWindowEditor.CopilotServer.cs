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
using System.Threading;
using com.IvanMurzak.McpPlugin.Common.Model;
using com.IvanMurzak.McpPlugin.Common.Utils;
using com.IvanMurzak.ReflectorNet.Utils;
using com.AtelierAI.Unity.Copilot.Editor.UI.Controls;
using Microsoft.Extensions.Logging;
using R3;
using UnityEngine;
using UnityEngine.UIElements;
using static com.IvanMurzak.McpPlugin.Common.Consts.MCP.Server;

namespace com.AtelierAI.Unity.Copilot.Editor.UI
{
    public partial class MainWindowEditor
    {
        private void SetupMcpServerSection(VisualElement root)
        {
            var btnStartStop = root.Q<Button>("btnStartStopServer") ?? throw new InvalidOperationException("Server start/stop button not found.");
            var statusCircle = root.Q<VisualElement>("mcpServerStatusCircle") ?? throw new InvalidOperationException("Server status circle not found.");
            var statusLabel = root.Q<Label>("mcpServerLabel") ?? throw new InvalidOperationException("Server status label not found.");

            var timelinePointMcpServer = root.Q<VisualElement>("TimelinePointMcpServer");
            if (timelinePointMcpServer != null)
                timelinePointMcpServer.tooltip = Tooltip_McpServerTimelineLabel;
            statusCircle.tooltip = Tooltip_McpServerTimelineLabel;
            statusLabel.tooltip = Tooltip_McpServerTimelineLabel;

            Observable.CombineLatest(
                    source1: CopilotServerManager.ServerStatus,
                    source2: UnityCopilotPluginEditor.IsConnected,
                    resultSelector: CombineCopilotServerStatus)
                .ThrottleLast(TimeSpan.FromMilliseconds(50))
                .ObserveOnCurrentSynchronizationContext()
                .Subscribe(status => FetchMcpServerData(status, btnStartStop, statusCircle, statusLabel))
                .AddTo(_disposables);

            btnStartStop.RegisterCallback<ClickEvent>(evt => HandleServerButton(btnStartStop, statusLabel));

            // server authorization configuration UI elements
            var labelAuthorizationToken = root.Q<Label>("labelAuthorizationToken");
            var segmentAuthorization = root.Q<VisualElement>("segmentAuthorization");
            var inputAuthorizationToken = root.Q<TextField>("inputAuthorizationToken");
            var tokenSection = root.Q<VisualElement>("tokenSection");
            var btnGenerateToken = root.Q<Button>("btnGenerateToken");

            if (segmentAuthorization == null || inputAuthorizationToken == null
                || tokenSection == null || btnGenerateToken == null)
            {
                Debug.LogError("One or more authorization UI elements not found in UXML: " +
                    $"segmentAuthorization={segmentAuthorization != null}, " +
                    $"inputAuthorizationToken={inputAuthorizationToken != null}, " +
                    $"tokenSection={tokenSection != null}, " +
                    $"btnGenerateToken={btnGenerateToken != null}");
                return;
            }

            var authControl = new SegmentedControl("none", "required");
            authControl.SetTooltips(Tooltip_ToggleAuthNone, Tooltip_ToggleAuthRequired);
            segmentAuthorization.Add(authControl);

            inputAuthorizationToken.isPasswordField = true;
            inputAuthorizationToken.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode == KeyCode.C && (evt.ctrlKey || evt.commandKey))
                {
                    GUIUtility.systemCopyBuffer = inputAuthorizationToken.value;
                    evt.StopPropagation();
                }
            });

            if (labelAuthorizationToken != null) labelAuthorizationToken.tooltip = Tooltip_LabelAuthorizationToken;
            btnGenerateToken.tooltip = Tooltip_BtnGenerateToken;

            var authOption = UnityCopilotPluginEditor.AuthOption;
            authControl.SetValueWithoutNotify(authOption == AuthOption.none ? 0 : 1);
            inputAuthorizationToken.SetValueWithoutNotify(UnityCopilotPluginEditor.Token ?? string.Empty);
            SetTokenFieldsVisible(inputAuthorizationToken, tokenSection, authOption == AuthOption.required);

            authControl.RegisterCallback<ChangeEvent<int>>(evt =>
            {
                if (evt.newValue == 0)
                {
                    ApplyServerSettingAndRestart(() =>
                    {
                        UnityCopilotPluginEditor.AuthOption = AuthOption.none;
                    });
                    SetTokenFieldsVisible(inputAuthorizationToken, tokenSection, false);
                }
                else
                {
                    ApplyServerSettingAndRestart(() =>
                    {
                        UnityCopilotPluginEditor.AuthOption = AuthOption.required;
                    });
                    SetTokenFieldsVisible(inputAuthorizationToken, tokenSection, true);
                }
            });

            inputAuthorizationToken.RegisterCallback<FocusOutEvent>(_ =>
            {
                var newToken = inputAuthorizationToken.value;
                if (newToken == UnityCopilotPluginEditor.Token)
                    return;

                ApplyServerSettingAndRestart(() =>
                {
                    UnityCopilotPluginEditor.Token = newToken;
                });
            });

            btnGenerateToken.RegisterCallback<ClickEvent>(_ =>
            {
                var newToken = UnityCopilotPlugin.GenerateToken();
                inputAuthorizationToken.SetValueWithoutNotify(newToken);

                ApplyServerSettingAndRestart(() =>
                {
                    UnityCopilotPluginEditor.Token = newToken;
                });
            });

            // ── Network Safety (fail-closed by default) ─────────────────────────────
            // Build the UI programmatically and append it to the same flex column that
            // hosts tokenSection so we don't have to introduce a new UXML asset. The
            // three toggles map 1:1 to the static properties on UnityCopilotPluginEditor;
            // each change is persisted immediately so the next StartServer() call sees
            // the new state.
            BuildNetworkSafetySection(tokenSection.parent);
        }

        private static void BuildNetworkSafetySection(VisualElement parent)
        {
            if (parent == null) return;
            // If a previous CreateGUI cycle already added the section, recreate it idempotently
            // so the toggles always reflect the current persisted state.
            var existing = parent.Q<VisualElement>("networkSafetySection");
            if (existing != null) parent.Remove(existing);

            var section = new VisualElement { name = "networkSafetySection" };
            section.style.flexDirection = FlexDirection.Column;
            section.style.marginTop = 4;
            section.style.marginBottom = 2;

            var header = new Label("Network Safety");
            header.AddToClassList("section-desc");
            header.style.marginBottom = 2;
            header.tooltip =
                "Fail-closed defaults: by default the server only binds to localhost and " +
                "refuses to start on a non-loopback URL until you opt in here. Existing " +
                "configurations that already used a LAN address before this update were " +
                "auto-granted on first load so behaviour is preserved — a one-time warning " +
                "is written to the Editor console.";
            section.Add(header);

            var toggleAllowLan = new Toggle("Allow LAN Bind (HTTP)")
            {
                value = UnityCopilotPluginEditor.AllowLanBind,
                tooltip =
                    "When OFF (safe default): the plugin refuses to start the server if " +
                    "the Server URL points at 0.0.0.0, ::, or a non-loopback IP/hostname.\n\n" +
                    "When ON: the plugin will start the server bound to whatever interface the " +
                    "Server URL specifies. Other machines on your network may be able to reach " +
                    "the server. Always pair this with a strong token."
            };
            toggleAllowLan.RegisterValueChangedCallback(evt =>
            {
                UnityCopilotPluginEditor.AllowLanBind = evt.newValue;
                UnityCopilotPluginEditor.Instance.Save();
            });
            section.Add(toggleAllowLan);

            var toggleAllowInsecure = new Toggle("Allow Insecure Remote HTTP")
            {
                value = UnityCopilotPluginEditor.AllowInsecureRemoteHttp,
                tooltip =
                    "When OFF (safe default): the plugin logs a warning every time it emits an " +
                    "AI-agent client configuration that points at a remote host over plain " +
                    "http:// (no TLS).\n\n" +
                    "When ON: the warning is silenced. Tokens and tool payloads sent over " +
                    "plain HTTP are visible to anyone sniffing the network — only enable this " +
                    "for trusted local networks where TLS is not available."
            };
            toggleAllowInsecure.RegisterValueChangedCallback(evt =>
            {
                UnityCopilotPluginEditor.AllowInsecureRemoteHttp = evt.newValue;
                UnityCopilotPluginEditor.Instance.Save();
            });
            section.Add(toggleAllowInsecure);

            var toggleForceToken = new Toggle("Force Token When LAN Bind")
            {
                value = UnityCopilotPluginEditor.ForceTokenWhenLanBind,
                tooltip =
                    "When ON (safe default): enabling 'Allow LAN Bind' is not enough to start " +
                    "the server — you must also set Authorization to 'required' and have a " +
                    "non-empty token. Prevents accidental exposure of an unauthenticated " +
                    "endpoint to the LAN.\n\n" +
                    "When OFF: LAN-bound servers may run with auth=none. Not recommended."
            };
            toggleForceToken.RegisterValueChangedCallback(evt =>
            {
                UnityCopilotPluginEditor.ForceTokenWhenLanBind = evt.newValue;
                UnityCopilotPluginEditor.Instance.Save();
            });
            section.Add(toggleForceToken);

            parent.Add(section);
        }

        private void ApplyServerSettingAndRestart(Action applySetting)
        {
            var wasRunning = CopilotServerManager.IsRunning;
            applySetting();
            UnityCopilotPluginEditor.Instance.Save();
            RestartServerIfWasRunning(wasRunning);
        }

        internal static CopilotServerStatus CombineCopilotServerStatus(CopilotServerStatus status, bool isConnected)
        {
            if (isConnected && status != CopilotServerStatus.Running)
                return CopilotServerStatus.External;

            return status;
        }

        internal static string GetServerButtonText(CopilotServerStatus status) => status switch
        {
            CopilotServerStatus.Running => "Stop",
            CopilotServerStatus.Starting => "Starting...",
            CopilotServerStatus.Stopping => "Stopping...",
            CopilotServerStatus.External => "External",
            _ => "Start"
        };

        // The Node.js server exposes a single HTTP transport (REST + WebSocket), so the
        // status label no longer carries a transport-mode suffix.
        internal static string GetServerLabelText(CopilotServerStatus status) => status switch
        {
            CopilotServerStatus.Running => "Server: Running",
            CopilotServerStatus.Starting => "Server: Starting...",
            CopilotServerStatus.Stopping => "Server: Stopping...",
            CopilotServerStatus.External => "Server: External",
            _ => "Server"
        };

        internal static string GetServerStatusClass(CopilotServerStatus status) => status switch
        {
            CopilotServerStatus.Running => USS_Connected,
            CopilotServerStatus.Starting or CopilotServerStatus.Stopping => USS_Connecting,
            CopilotServerStatus.External => USS_External,
            _ => USS_Disconnected
        };

        internal static bool IsServerButtonEnabled(CopilotServerStatus status) =>
            status == CopilotServerStatus.Running || status == CopilotServerStatus.Stopped;

        private static void HandleServerButton(Button btnStartStop, Label statusLabel)
        {
            // Disable button immediately to prevent double-clicks
            btnStartStop.SetEnabled(false);

            try
            {
                if (CopilotServerManager.IsRunning)
                {
                    // User is stopping the server - remember not to auto-start
                    UnityCopilotPluginEditor.KeepServerRunning = false;
                    UnityCopilotPluginEditor.Instance.Save();
                    statusLabel.text = "Server: Stopping...";
                    CopilotServerManager.StopServer();
                }
                else
                {
                    // User is starting the server - remember to auto-start
                    UnityCopilotPluginEditor.KeepServerRunning = true;
                    UnityCopilotPluginEditor.Instance.Save();
                    statusLabel.text = "Server: Starting...";
                    CopilotServerManager.StartServer();
                }
            }
            catch
            {
                // Re-enable button on exception to avoid infinite lock
                btnStartStop.SetEnabled(true);
                throw;
            }
        }

        private long SetMcpServerData(McpServerData? data, CopilotServerStatus status, Button btnStartStop, VisualElement statusCircle, Label statusLabel)
        {
            var version = Interlocked.Increment(ref _mcpServerDataVersion);
            if (Logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Trace))
                Logger.LogTrace("Setting server data: {status}, Data: {data}", status, data?.ToPrettyJson() ?? "null");

            btnStartStop.text = GetServerButtonText(status);
            var isStart = status == CopilotServerStatus.Stopped;
            btnStartStop.EnableInClassList("btn-primary", isStart);
            btnStartStop.EnableInClassList("btn-secondary", !isStart);
            btnStartStop.SetEnabled(IsServerButtonEnabled(status));
            statusLabel.text = GetServerLabelText(status);
            SetStatusIndicator(statusCircle, GetServerStatusClass(status));
            return version;
        }

        private void FetchMcpServerData(CopilotServerStatus status, Button btnStartStop, VisualElement statusCircle, Label statusLabel)
        {
            // Update UI immediately with current status; capture the version atomically so that
            // the async result can detect if a newer update has superseded it.
            var fetchVersion = SetMcpServerData(null, status, btnStartStop, statusCircle, statusLabel);

            // Then try to fetch additional data asynchronously
            var mcpPluginInstance = UnityCopilotPluginEditor.Instance.McpPluginInstance;
            if (mcpPluginInstance == null)
            {
                Logger.LogDebug("Cannot fetch server data: McpPluginInstance is null");
                return;
            }

            var mcpManagerHub = mcpPluginInstance.McpManagerHub;
            if (mcpManagerHub == null)
            {
                Logger.LogDebug("Cannot fetch server data: McpManagerHub is null");
                return;
            }

            var task = mcpManagerHub.GetMcpServerData();
            if (task == null)
            {
                Logger.LogDebug("Cannot fetch server data: GetMcpServerData returned null");
                return;
            }

            task.ContinueWith(t =>
            {
                if (Interlocked.Read(ref _mcpServerDataVersion) != fetchVersion)
                {
                    Logger.LogTrace("Skipping server data update because a newer update was applied at {time}",
                        DateTime.UtcNow);
                    return;
                }
                MainThread.Instance.Run(() =>
                {
                    // Second check: close the TOCTOU window between the thread-pool check above
                    // and the main-thread callback execution.
                    if (Interlocked.Read(ref _mcpServerDataVersion) != fetchVersion)
                        return;
                    if (t.IsCompletedSuccessfully)
                    {
                        var data = t.Result;
                        SetMcpServerData(data, status, btnStartStop, statusCircle, statusLabel);
                    }
                    else if (t.IsFaulted)
                    {
                        Logger.LogDebug("Failed to fetch server data: {error}", t.Exception?.Message ?? "Unknown error");
                    }
                });
            });
        }
    }
}
