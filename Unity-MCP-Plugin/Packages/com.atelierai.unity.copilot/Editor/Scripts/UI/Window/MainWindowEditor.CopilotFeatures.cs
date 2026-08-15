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
using System.Linq;
using System.Threading;
using com.IvanMurzak.McpPlugin.Common.Utils;
using com.IvanMurzak.ReflectorNet.Utils;
using com.AtelierAI.Unity.Copilot.Editor.Utils;
using Microsoft.Extensions.Logging;
using R3;
using UnityEngine;
using UnityEngine.UIElements;

namespace com.AtelierAI.Unity.Copilot.Editor.UI
{
    public partial class MainWindowEditor
    {
        private void SetupAiAgentSection(VisualElement root)
        {
            UnityCopilotPluginEditor.PluginProperty
                .WhereNotNull()
                .Subscribe(plugin =>
            {
                plugin.McpManager.OnClientConnected
                    .Subscribe(data =>
                    {
                        Logger.LogInformation("On AI agent connected: {clientName} ({clientVersion})",
                            data.ClientName, data.ClientVersion);

                        if (Logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Trace))
                            Logger.LogTrace("AI Agent Data: {data}", data.ToPrettyJson());
                    })
                    .AddTo(_disposables);

                plugin.McpManager.OnClientDisconnected
                    .Subscribe(mcpClientData =>
                    {
                        Logger.LogInformation("On AI agent disconnected: {clientName} ({clientVersion})",
                            mcpClientData.ClientName, mcpClientData.ClientVersion);
                    })
                    .AddTo(_disposables);

                plugin.McpManager.OnClientsChanged
                    .ObserveOnCurrentSynchronizationContext()
                    .Subscribe(mcpClients =>
                    {
                        Logger.LogDebug("On AI agents changed: {count} clients", mcpClients.Count);

                        var connectedAgents = mcpClients.Where(c => c.IsConnected).ToList();
                        if (connectedAgents.Count == 0)
                        {
                            Logger.LogDebug("No connected AI agents found in clients list.");
                            SetAiAgentStatus(false);
                            return;
                        }

                        SetAiAgentStatus(true, connectedAgents.Select(a => $"AI agent: {a.ClientName} ({a.ClientVersion})"));
                    })
                    .AddTo(_disposables);

                FetchAiAgentData();
            }).AddTo(_disposables);

            UnityCopilotPluginEditor.IsConnected
                .Where(isConnected => isConnected)
                .ObserveOnCurrentSynchronizationContext()
                .Subscribe(_ => FetchAiAgentData())
                .AddTo(_disposables);

            var btnStartStopMcpServer = root.Q<Button>("btnStartStopServer") ?? throw new InvalidOperationException("MCP Server start/stop button not found.");
            btnStartStopMcpServer.tooltip = "Start or stop the local MCP server.";
        }

        private void FetchAiAgentData(int retryCount = 3, int retryDelayMs = 3000)
        {
            var mcpPluginInstance = UnityCopilotPluginEditor.Instance.McpPluginInstance;
            if (mcpPluginInstance == null)
            {
                Logger.LogDebug("Cannot fetch AI agent data: McpPluginInstance is null");
                return;
            }

            var mcpManagerHub = mcpPluginInstance.McpManagerHub;
            if (mcpManagerHub == null)
            {
                Logger.LogDebug("Cannot fetch AI agent data: McpManagerHub is null");
                return;
            }

            var task = mcpManagerHub.GetMcpClientData();
            if (task == null)
            {
                Logger.LogDebug("Cannot fetch AI agent data: GetMcpClientData returned null");
                return;
            }

            // Claim a unique version for this fetch so the async result can detect if a newer
            // update (e.g. from OnClientsChanged or another FetchAiAgentData call) has superseded it.
            var fetchVersion = Interlocked.Increment(ref _aiAgentDataVersion);

            task.ContinueWith(t =>
            {
                if (Interlocked.Read(ref _aiAgentDataVersion) != fetchVersion)
                {
                    Logger.LogTrace("Skipping AI agent data update because a newer update was applied at {time}",
                        DateTime.UtcNow);
                    return;
                }
                MainThread.Instance.Run(() =>
                {
                    // Second check: close the TOCTOU window between the thread-pool check above
                    // and the main-thread callback execution.
                    if (Interlocked.Read(ref _aiAgentDataVersion) != fetchVersion)
                        return;
                    if (t.IsCompletedSuccessfully)
                    {
                        var clients = t.Result;
                        var connectedAgents = clients.Where(c => c.IsConnected).ToList();
                        var isConnected = connectedAgents.Count > 0;
                        SetAiAgentStatus(isConnected, isConnected
                            ? connectedAgents.Select(a => $"AI agent: {a.ClientName} ({a.ClientVersion})")
                            : null);

                        // If AI agent is not connected but Unity is, retry after delay.
                        // The AI agent may need time to re-establish its session after Unity reconnects.
                        if (!isConnected && retryCount > 0 && UnityCopilotPluginEditor.IsConnected.CurrentValue)
                        {
                            Logger.LogDebug("AI agent not connected yet, scheduling retry ({retriesLeft} left)", retryCount);
                            Observable.Timer(TimeSpan.FromMilliseconds(retryDelayMs))
                                .ObserveOnCurrentSynchronizationContext()
                                .Subscribe(_ => FetchAiAgentData(retryCount - 1, retryDelayMs))
                                .AddTo(_disposables);
                        }
                    }
                    else if (t.IsFaulted)
                    {
                        Logger.LogDebug("Failed to fetch AI agent data: {error}", t.Exception?.Message ?? "Unknown error");
                        SetAiAgentStatus(false);
                    }
                    else
                    {
                        SetAiAgentStatus(false, new[] { "AI agent: Not found" });
                    }
                });
            });
        }

        private void SetupSocialButtons(VisualElement root)
        {
            var discordIcon = EditorAssetLoader.LoadAssetAtPath<Texture2D>(_discordIconPaths);
            var githubIcon = EditorAssetLoader.LoadAssetAtPath<Texture2D>(_githubIconPaths);
            var starIcon = EditorAssetLoader.LoadAssetAtPath<Texture2D>(_starIconPaths);

            SetupSocialButton(root, "btnGitHubStar", "btnGitHubStarIcon", starIcon, URL_GitHub, "Star on GitHub");
            SetupSocialButton(root, "btnGitHubIssue", "btnGitHubIssueIcon", githubIcon, URL_GitHubIssues, "Report an issue on GitHub");
            SetupSocialButton(root, "btnDiscordHelp", "btnDiscordHelpIcon", discordIcon, URL_Discord, "Get help on Discord");
        }

        private static void SetupSocialButton(VisualElement root, string buttonName, string iconName, Texture2D? icon, string url, string tooltip)
        {
            var button = root.Q<Button>(buttonName);
            if (button == null)
                return;

            var iconElement = root.Q<VisualElement>(iconName);
            if (iconElement != null)
            {
                iconElement.style.backgroundImage = icon;
                iconElement.style.display = icon != null ? DisplayStyle.Flex : DisplayStyle.None;
            }

            button.tooltip = tooltip;
            button.RegisterCallback<ClickEvent>(evt => Application.OpenURL(url));
        }

        private static void SetupDebugButtons(VisualElement root)
        {
            var btnCheckSerialization = root.Q<Button>("btnCheckSerialization");
            if (btnCheckSerialization != null)
            {
                btnCheckSerialization.tooltip = "Open Serialization Check window";
                btnCheckSerialization.RegisterCallback<ClickEvent>(evt => SerializationCheckWindow.ShowWindow());
            }
        }
    }
}
