/*
┌────────────────────────────────────────────────────────────────────────┐
│  Author: Ivan Murzak (https://github.com/IvanMurzak)                   │
│  Repository: GitHub (https://github.com/IvanMurzak/MCP-Plugin-dotnet)  │
│  Copyright (c) 2025 Ivan Murzak                                        │
│  Licensed under the Apache License, Version 2.0.                       │
│  See the LICENSE file in the project root for more information.        │
└────────────────────────────────────────────────────────────────────────┘
*/

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using com.AtelierAI.Uco.Framework.Common.Hub.Client;
using com.AtelierAI.Uco.Framework.Common.Model;
using com.IvanMurzak.ReflectorNet;
using Microsoft.Extensions.Logging;
using R3;

namespace com.AtelierAI.Uco.Framework
{
    public class UcoManager : IPluginManager, IClientPluginManager
    {
        protected readonly ILogger _logger;
        protected readonly Reflector _reflector;
        // Volatile ensures the reference write is visible to all threads without CPU/JIT caching.
        // Thread-safety contract: arrays are never mutated after assignment — only replaced atomically.
        // Readers always observe either the previous or next snapshot, never a torn state.
        private volatile IReadOnlyList<UcoClientData> _activeClients = Array.Empty<UcoClientData>();
        private readonly Subject<Unit> _onForceDisconnect = new();
        private readonly Subject<UcoClientData> _onClientConnected = new();
        private readonly Subject<UcoClientData> _onClientDisconnected = new();
        private readonly Subject<IReadOnlyList<UcoClientData>> _onClientsChanged = new();

        readonly IToolManager? _tools;
        readonly IPromptManager? _prompts;
        readonly IResourceManager? _resources;
        readonly ISystemToolManager? _systemTools;

        public Reflector Reflector => _reflector;
        public IToolManager? ToolManager => _tools;
        public IPromptManager? PromptManager => _prompts;
        public IResourceManager? ResourceManager => _resources;
        public ISystemToolManager? SystemToolManager => _systemTools;

        public IClientToolHub? ToolHub => _tools;
        public IClientPromptHub? PromptHub => _prompts;
        public IClientResourceHub? ResourceHub => _resources;
        public IClientSystemToolHub? SystemToolHub => _systemTools;

        public IReadOnlyList<UcoClientData> ActiveClients => _activeClients;
        public Observable<Unit> OnForceDisconnect => _onForceDisconnect.AsObservable();
        public Observable<UcoClientData> OnClientConnected => _onClientConnected.AsObservable();
        public Observable<UcoClientData> OnClientDisconnected => _onClientDisconnected.AsObservable();
        public Observable<IReadOnlyList<UcoClientData>> OnClientsChanged => _onClientsChanged.AsObservable();

        public UcoManager(
            ILogger<UcoManager> logger,
            Reflector reflector,
            IToolManager? tools = null,
            IPromptManager? prompts = null,
            IResourceManager? resources = null,
            ISystemToolManager? systemTools = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _logger.LogTrace("Ctor");

            _reflector = reflector ?? throw new ArgumentNullException(nameof(reflector));

            _tools = tools;
            _prompts = prompts;
            _resources = resources;
            _systemTools = systemTools;
        }

        public Task OnPluginClientConnected(UcoClientData connectedClient, UcoClientData[] allActiveClients)
        {
            _activeClients = allActiveClients;
            _onClientConnected.OnNext(connectedClient);
            _onClientsChanged.OnNext(allActiveClients);
            return Task.CompletedTask;
        }

        public Task OnPluginClientDisconnected(UcoClientData disconnectedClient, UcoClientData[] remainingClients)
        {
            _activeClients = remainingClients;
            _onClientDisconnected.OnNext(disconnectedClient);
            _onClientsChanged.OnNext(remainingClients);
            return Task.CompletedTask;
        }

        public Task OnInitialClientData(UcoClientData[] allActiveClients)
        {
            _activeClients = allActiveClients;
            _onClientsChanged.OnNext(allActiveClients);
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            _logger.LogDebug("{method} called.", nameof(Dispose));

            _tools?.Dispose();
            _prompts?.Dispose();
            _resources?.Dispose();

            _logger.LogDebug("{method} completed.", nameof(Dispose));
        }

        public Task ForceDisconnect(string? reason = null)
        {
            _onForceDisconnect.OnNext(Unit.Default);
            return Task.CompletedTask;
        }
    }
}
