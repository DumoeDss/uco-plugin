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
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using com.AtelierAI.Uco.Framework;
using com.AtelierAI.Unity.Copilot.Editor.Services;
using com.AtelierAI.Unity.Copilot.Editor.Utils;
using Microsoft.Extensions.Logging;
using R3;
using UnityEngine;
using UnityEngine.UIElements;
using static com.AtelierAI.Uco.Framework.Common.Consts.MCP.Server;
using LogLevel = com.AtelierAI.Unity.Copilot.Runtime.Utils.LogLevel;

namespace com.AtelierAI.Unity.Copilot.Editor.UI
{
    public partial class MainWindowEditor
    {
        private static readonly string[] _windowUxmlPaths = EditorAssetLoader.GetEditorAssetPaths("Editor/UI/uxml/MainWindow.uxml");
        private static readonly string[] _windowUssPaths = EditorAssetLoader.GetEditorAssetPaths("Editor/UI/uss/MainWindow.uss");

        private static readonly string[] _discordIconPaths = EditorAssetLoader.GetEditorAssetPaths("Editor/Gizmos/discord_icon.png");
        private static readonly string[] _githubIconPaths = EditorAssetLoader.GetEditorAssetPaths("Editor/Gizmos/github_icon.png");
        private static readonly string[] _starIconPaths = EditorAssetLoader.GetEditorAssetPaths("Editor/Gizmos/star_icon.png");

        public const string USS_Connected = "status-indicator-circle-online";
        public const string USS_Connecting = "status-indicator-circle-connecting";
        public const string USS_Disconnected = "status-indicator-circle-disconnected";
        public const string USS_External = "status-indicator-circle-external";

        private static readonly string[] AllStatusClasses =
        {
            USS_Connected,
            USS_Connecting,
            USS_Disconnected,
            USS_External
        };

        private const string ServerButtonText_Connect = "Connect";
        private const string ServerButtonText_Disconnect = "Disconnect";
        private const string ServerButtonText_Stop = "Stop";

        private const string URL_GitHub = "https://github.com/IvanMurzak/Unity-MCP";
        private const string URL_GitHubIssues = "https://github.com/IvanMurzak/uco-plugin/issues";
        private const string URL_Discord = "https://discord.gg/cfbdMZX99G";

        // ── Shared tooltip building blocks ──────────────────────────────────────────
        //
        // These blocks are embedded verbatim inside the per-element tooltips below so
        // that each tooltip is self-contained yet the authoritative text lives in one place.

        private const string Tooltip_AuthorizationTokenConcept =
            "The authorization token is a shared secret key. When required, every AI agent " +
            "must include this token in its server configuration. The server rejects any " +
            "connection that does not supply the correct token.\n\n" +
            "Treat this token like a password — do not share it publicly or commit it to version control.";

        // ── Per-element tooltips ─────────────────────────────────────────────────────

        private const string Tooltip_LabelAuthorizationToken =
            "Controls whether the server requires a secret token to accept connections " +
            "from AI agents.\n\n" +
            Tooltip_AuthorizationTokenConcept;

        private const string Tooltip_ToggleAuthNone =
            "Local deployment — no authorization token required.\n\n" +
            "The server accepts any connection without checking a token. This is safe when " +
            "both Unity and the AI agent run on the same machine and the server port is not " +
            "reachable from the network.\n\n" +
            "Use this when:\n" +
            "  • Unity, the server, and the AI agent are all on the same computer.\n" +
            "  • No other machines need to reach the server.\n\n" +
            "⚠ Do not use this if the server port is exposed to other machines or the internet.\n\n" +
            "About authorization tokens:\n" +
            Tooltip_AuthorizationTokenConcept;

        private const string Tooltip_ToggleAuthRequired =
            "Remote deployment — authorization token required.\n\n" +
            "Every AI agent must supply the correct token in its server configuration. " +
            "The server will reject any connection that does not include a valid token.\n\n" +
            "Use this when:\n" +
            "  • The server runs on a different machine from the AI agent.\n" +
            "  • The server endpoint is reachable over a network.\n\n" +
            "After enabling, generate a secure token with the 'New' button, then copy it into " +
            "your AI agent's server configuration.\n\n" +
            "About authorization tokens:\n" +
            Tooltip_AuthorizationTokenConcept;

        private const string Tooltip_ToolsCountLabel =
            "A Tool is an executable function the AI agent can invoke to perform an action — " +
            "creating GameObjects, modifying assets, running scripts, reading scene data, and more. " +
            "Tools are the primary instrument through which the AI interacts with your Unity project.\n\n" +
            "Every active tool contributes tokens to the AI's context window. A smaller context means " +
            "the AI has more room to reason, producing better and more accurate results. " +
            "Disable tools you don't use to keep context consumption low.";

        private const string Tooltip_PromptsCountLabel =
            "A Prompt is a slash-command the user can invoke directly from the AI client " +
            "(e.g. /setup-basic-scene). Each prompt can accept arguments, and when triggered it " +
            "injects a pre-written instruction into the AI context, guiding the AI on what to do next.\n\n" +
            "Prompts do not passively consume context tokens — they only use context at the moment " +
            "a user explicitly invokes them.";

        private const string Tooltip_ResourcesCountLabel =
            "A Resource is a named, read-only data provider the AI agent can query to " +
            "understand your Unity project. Resources expose structured information such as scene " +
            "hierarchy, asset metadata, and project settings without performing any actions or " +
            "modifications.\n\n" +
            "Resources are fetched on-demand when the AI needs specific project information. " +
            "Unlike tools, they never modify any state.";

        private const string Tooltip_UnityTimelineLabel =
            "Unity Editor connection status. This indicates whether the Unity Editor is connected " +
            "to the server.\n\n" +
            "Unity must stay connected for any AI interaction to work. When connected, the AI agent " +
            "can invoke tools, read resources, and execute operations inside your Unity project.\n\n" +
            "If Unity is disconnected, no AI client will be able to reach the Editor — tools will " +
            "fail and the AI agent will have no way to interact with your project.\n\n" +
            "Use the Connect / Disconnect button to control the connection manually.";

        private const string Tooltip_McpServerTimelineLabel =
            "The server is the gateway between AI and Unity. Every AI operation that interacts " +
            "with Unity — tools, prompts, resources, and skills — goes through this server.\n\n" +
            "It translates AI requests into Unity API calls, enabling the AI agent to create " +
            "GameObjects, modify assets, run scripts, read scene data, execute tests, and more.\n\n" +
            "The server runs as a local process managed by the plugin. It starts automatically " +
            "when Unity opens and can be controlled manually with the Start / Stop button.\n\n" +
            "Without a running server, no AI client can interact with your Unity project.";

        private const string Tooltip_AiAgentTimelineLabel =
            "The AI agent connection indicator shows whether an external AI client (Claude, Cursor, " +
            "Copilot, etc.) is currently connected to the server.\n\n" +
            "It is completely normal for the AI agent to appear disconnected:\n" +
            "  • With 'Cloud' connection mode the AI agent connects on-demand and may disconnect " +
            "between interactions.\n" +
            "  • With a local server the AI agent connects only when it needs to invoke a tool " +
            "or read a resource, then may drop the session.\n\n" +
            "The AI agent will always reconnect automatically whenever it needs to perform an " +
            "action in Unity — no manual intervention is required.\n\n" +
            "If you use AI Skills (slash-commands like /setup-basic-scene), the AI agent connection " +
            "is not needed at all — skills are injected directly into the AI context without an " +
            "active server session.\n\n" +
            "Important: Unity itself should always remain connected to the server (the top " +
            "'Unity' indicator). The AI agent indicator is informational only.";

        private const string Tooltip_BtnGenerateToken =
            "Generate a new cryptographically secure random token.\n\n" +
            "Uses a cryptographic RNG to produce 32 bytes (256 bits) of randomness encoded as " +
            "URL-safe Base64 — suitable for production-level authentication.\n\n" +
            "Steps after generating:\n" +
            "  1. The new token is saved automatically to your project configuration.\n" +
            "  2. The server is restarted to apply the new token.\n" +
            "  3. Copy the token from the input field next to this button.\n" +
            "  4. Open your AI agent's server configuration and paste the token into the " +
            "authorization field.\n\n" +
            "⚠ Generating a new token immediately invalidates the previous one. Every AI agent " +
            "must be updated with the new token before it can connect again.";

        private VisualElement? _aiAgentLabelsContainer;
        private VisualElement? _aiAgentStatusCircle;

        private DeviceAuthFlow? _deviceAuthFlow;

        private long _mcpServerDataVersion;
        private long _aiAgentDataVersion;

        protected override void OnGUICreated(VisualElement root)
        {
            _disposables.Clear();

            SetupSettingsSection(root);
            SetupConnectionSection(root);
            SetupConnectionModeToggle(root);
            SetupCloudAuthSection(root);
            SetupConnectionAlerts(root);
            SetupMcpServerSection(root);
            SetupAiAgentSection(root);
            SetupDebugButtons(root);
            EnableSmoothFoldoutTransitions(root);
        }

        #region Status Indicator Helpers

        private static void SetStatusIndicator(VisualElement element, string statusClass)
        {
            foreach (var cls in AllStatusClasses)
                element.RemoveFromClassList(cls);
            element.AddToClassList(statusClass);
        }

        internal static string GetConnectionStatusClass(ConnectionState state, bool keepConnected) => state switch
        {
            ConnectionState.Connected when keepConnected => USS_Connected,
            _ when keepConnected => USS_Connecting,
            _ => USS_Disconnected
        };

        internal static string GetConnectionStatusText(ConnectionState state, bool keepConnected) => state switch
        {
            ConnectionState.Connected when keepConnected => "Connected",
            _ when keepConnected => "Connecting...",
            _ => "Disconnected"
        };

        internal static string GetButtonText(ConnectionState state, bool keepConnected) => state switch
        {
            ConnectionState.Connected when keepConnected => ServerButtonText_Disconnect,
            _ when keepConnected => ServerButtonText_Stop,
            _ => ServerButtonText_Connect
        };

        private void SetAiAgentStatus(bool isConnected, IEnumerable<string>? labels = null)
        {
            Interlocked.Increment(ref _aiAgentDataVersion);

            if (_aiAgentStatusCircle == null)
            {
                Logger.LogError("{field} is not initialized, cannot update AI agent status", nameof(_aiAgentStatusCircle));
                return;
            }
            if (_aiAgentLabelsContainer == null)
            {
                Logger.LogError("{field} is not initialized, cannot update AI agent status", nameof(_aiAgentLabelsContainer));
                return;
            }

            SetStatusIndicator(_aiAgentStatusCircle, isConnected ? USS_Connected : USS_Disconnected);
            _aiAgentStatusCircle.tooltip = Tooltip_AiAgentTimelineLabel;

            _aiAgentLabelsContainer.tooltip = Tooltip_AiAgentTimelineLabel;
            _aiAgentLabelsContainer.Clear();
            var labelList = labels?.ToList();
            if (labelList == null || labelList.Count == 0)
            {
                var lbl = new Label("AI agent");
                lbl.AddToClassList("timeline-label");
                lbl.tooltip = Tooltip_AiAgentTimelineLabel;
                _aiAgentLabelsContainer.Add(lbl);
            }
            else
            {
                foreach (var text in labelList)
                {
                    var lbl = new Label(text);
                    lbl.AddToClassList("timeline-label");
                    lbl.tooltip = Tooltip_AiAgentTimelineLabel;
                    _aiAgentLabelsContainer.Add(lbl);
                }
            }
        }

        #endregion

        #region Header

        private void SetupSettingsSection(VisualElement root)
        {
            // Populate in code rather than via UXML type="..." so the UXML importer
            // doesn't need to resolve com.AtelierAI.Unity.Copilot.Runtime at asset-import
            // time (the Runtime asmdef is gated by UNITY_COPILOT_READY, which is set later
            // by the NuGet resolver — resolving it during UXML import produces a
            // TypeLoadException on the very first package import).
            var dropdownLogLevel = root.Q<DropdownField>("dropdownLogLevel");
            dropdownLogLevel.choices = Enum.GetNames(typeof(LogLevel)).ToList();
            dropdownLogLevel.value = UnityCopilotPluginEditor.LogLevel.ToString();
            dropdownLogLevel.tooltip = "The minimum level of messages to log. Debug includes all messages, while Critical includes only the most severe.";
            dropdownLogLevel.RegisterValueChangedCallback(evt =>
            {
                UnityCopilotPluginEditor.LogLevel = Enum.TryParse<LogLevel>(evt.newValue, out var parsed)
                    ? parsed
                    : LogLevel.Warning;
                SaveChanges($"[Unity Co-Pilot] LogLevel Changed: {evt.newValue}");
            });

            var inputTimeoutMs = root.Q<IntegerField>("inputTimeoutMs");
            inputTimeoutMs.value = UnityCopilotPluginEditor.TimeoutMs;
            inputTimeoutMs.tooltip = $"Timeout for tool execution in milliseconds.\n\nMost tools only need a few seconds.\n\nSet this higher than your longest test execution time.\n\nImportant: Also update the '{Args.PluginTimeout}' argument in your AI agent configuration to match this value so your AI agent doesn't timeout before the tool completes.";
            inputTimeoutMs.RegisterCallback<FocusOutEvent>(evt =>
            {
                var newValue = Mathf.Max(1000, inputTimeoutMs.value);
                if (newValue == UnityCopilotPluginEditor.TimeoutMs)
                    return;

                if (newValue != inputTimeoutMs.value)
                    inputTimeoutMs.SetValueWithoutNotify(newValue);

                UnityCopilotPluginEditor.TimeoutMs = newValue;

                SaveChanges($"[Unity Co-Pilot] Timeout Changed: {newValue} ms");
                UnityBuildAndConnect();
            });

            root.Q<TextField>("currentVersion").value = UnityCopilotPlugin.Version;
        }

        #endregion

        #region Shared Helpers

        /// <summary>
        /// Subscribes to the combined connection state (ConnectionState + KeepConnected) with
        /// throttling and synchronization context marshaling.
        /// </summary>
        private void SubscribeToConnectionState(Action<ConnectionState, bool> onStateChanged)
        {
            UnityCopilotPluginEditor.PluginProperty
                .WhereNotNull()
                .Subscribe(plugin =>
            {
                Observable.CombineLatest(
                    UnityCopilotPluginEditor.ConnectionState, plugin.KeepConnected,
                    (state, keepConnected) => (state, keepConnected))
                .ThrottleLast(TimeSpan.FromMilliseconds(10))
                .ObserveOnCurrentSynchronizationContext()
                .SubscribeOnCurrentSynchronizationContext()
                .Subscribe(tuple =>
                {
                    var (state, keepConnected) = tuple;
                    onStateChanged(state, keepConnected);
                })
                .AddTo(_disposables);
            }).AddTo(_disposables);
        }

        #endregion

        // Relocated from MainWindowEditor.AiAgents.cs (retained after the AI Agent configurator
        // removal — these are generic token-field / server-restart helpers, not agent-specific).
        private static void SetTokenFieldsVisible(TextField tokenField, VisualElement actionsRow, bool visible)
        {
            tokenField.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
            actionsRow.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private void RestartServerIfWasRunning(bool wasRunning)
        {
            if (!wasRunning)
                return;

            CopilotServerManager.StopServer();

            CopilotServerManager.ServerStatus
                .Where(status => status == CopilotServerStatus.Stopped)
                .Take(1)
                .ObserveOnCurrentSynchronizationContext()
                .Subscribe(_ => CopilotServerManager.StartServer())
                .AddTo(_disposables);
        }
    }
}
