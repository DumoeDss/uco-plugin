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
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using com.AtelierAI.Uco.Framework.Common;
using com.IvanMurzak.ReflectorNet;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace com.AtelierAI.Uco.Framework
{
    /// <summary>
    /// Creates configured <see cref="ClientWebSocket"/> instances.
    /// Replaces the old <see cref="HubConnectionProvider"/> which built SignalR <c>HubConnection</c>.
    /// </summary>
    public class WebSocketConnectionProvider : IWebSocketConnectionProvider
    {
        private readonly ILogger _logger;
        private readonly Reflector _reflector;
        private readonly IServiceProvider _serviceProvider;
        private readonly JsonSerializerOptions _jsonSerializerOptions;

        public JsonSerializerOptions JsonSerializerOptions => _jsonSerializerOptions;

        public WebSocketConnectionProvider(ILogger<ClientWebSocket> logger, Reflector reflector, IServiceProvider serviceProvider)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _reflector = reflector ?? throw new ArgumentNullException(nameof(reflector));
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));

            // Build JsonSerializerOptions from Reflector settings — same configuration
            // that was previously applied to SignalR's JsonHubProtocolOptions.
            _jsonSerializerOptions = new JsonSerializerOptions();

            // CRITICAL: Add JsonStringEnumConverter BEFORE copying Reflector converters
            // so it takes precedence (first matching converter wins). This ensures
            // ResponseStatus and all enums serialize as lowercase strings ("error",
            // "success", "processing") matching the Node server's wire format.
            // netstandard2.1 cannot use [JsonStringEnumConverter] as an attribute.
            _jsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));

            JsonConfiguration.ConfigureJsonSerializer(_reflector, _jsonSerializerOptions);

            // CRITICAL: force camelCase AFTER copying the Reflector settings (which use
            // PropertyNamingPolicy = null → PascalCase keys). The Node server's wire
            // contract is camelCase for both envelope keys and payload fields — PascalCase
            // keys make every message unparseable server-side (observed live: the version
            // handshake request was rejected and the plugin timed out waiting for a reply).
            // Models carrying explicit [JsonPropertyName] are unaffected by the policy.
            _jsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        }

        public Task<(ClientWebSocket WebSocket, Uri Uri)> CreateConnectionAsync(string endpoint)
        {
            _logger.LogInformation("Creating WebSocket connection to {endpoint}", endpoint);

            try
            {
                var connectionConfig = _serviceProvider.GetRequiredService<IOptions<ConnectionConfig>>().Value;
                var instanceId = connectionConfig.InstanceId;
                var token = connectionConfig.Token;

                // Build URL: convert http(s):// to ws(s)://
                var baseUrl = connectionConfig.Host + endpoint;
                var wsUrl = ConvertHttpToWs(baseUrl);

                // Append query parameters
                var queryParts = new List<string>();
                if (!string.IsNullOrWhiteSpace(token))
                    queryParts.Add("access_token=" + Uri.EscapeDataString(token!));
                if (!string.IsNullOrWhiteSpace(instanceId))
                    queryParts.Add("instanceId=" + Uri.EscapeDataString(instanceId!));

                if (queryParts.Count > 0)
                {
                    var separator = wsUrl.IndexOf('?') >= 0 ? '&' : '?';
                    wsUrl = wsUrl + separator + string.Join("&", queryParts);
                }

                _logger.LogDebug("WebSocket URL: {url}", wsUrl);

                var ws = new ClientWebSocket();

                // Set Authorization header if token configured
                if (!string.IsNullOrWhiteSpace(token))
                    ws.Options.SetRequestHeader("Authorization", "Bearer " + token);

                // Set instance ID header for per-session routing
                if (!string.IsNullOrWhiteSpace(instanceId))
                    ws.Options.SetRequestHeader(Consts.MCP.Server.Headers.UcoInstanceId, instanceId);

                // No subprotocol — plain WebSocket, matching the Node server

                return Task.FromResult((ws, new Uri(wsUrl)));
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to create WebSocket connection. Exception: {message}", ex.Message);
                if (ex.InnerException != null)
                    _logger.LogError("Inner Exception: {message}", ex.InnerException.Message);
                throw;
            }
        }

        /// <summary>
        /// Convert <c>http://</c> → <c>ws://</c> and <c>https://</c> → <c>wss://</c>.
        /// SignalR handled this internally; with raw ClientWebSocket we must do it ourselves.
        /// </summary>
        internal static string ConvertHttpToWs(string url)
        {
            if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return "wss://" + url.Substring(8);
            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                return "ws://" + url.Substring(7);
            return url;
        }
    }
}
