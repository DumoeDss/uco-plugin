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
using System.Text.Json.Serialization;
using com.AtelierAI.Uco.Framework;
using com.AtelierAI.Uco.Framework.Common;
using com.AtelierAI.Uco.Framework.Common.Utils;
using com.AtelierAI.Unity.Copilot.Runtime.Utils;
using static com.AtelierAI.Uco.Framework.Common.Consts.MCP.Server;

namespace com.AtelierAI.Unity.Copilot
{
    public partial class UnityCopilotPlugin
    {
        protected readonly object configMutex = new();

        protected UnityConnectionConfig unityConnectionConfig = null!; // Set by subclass constructors

        public class UnityConnectionConfig : ConnectionConfig
        {
            public static string DefaultHost => $"http://localhost:{GeneratePortFromDirectory()}";

            public static List<CopilotFeature> DefaultTools => new();
            public static List<CopilotFeature> DefaultPrompts => new();
            public static List<CopilotFeature> DefaultResources => new();

            /// <summary>
            /// The local server URL. Serialized as "host" in JSON.
            /// </summary>
            [JsonPropertyName("host")]
            public string LocalHost { get; set; } = DefaultHost;

            /// <summary>
            /// The local auth token. Serialized as "token" in JSON.
            /// </summary>
            [JsonPropertyName("token")]
            public string? LocalToken { get; set; }

            [JsonIgnore]
            public override string Host
            {
                get => LocalHost;
                set => LocalHost = value;
            }

            [JsonIgnore]
            public override string? Token
            {
                get => LocalToken;
                set => LocalToken = value;
            }

            public LogLevel LogLevel { get; set; } = LogLevel.Warning;
            public bool KeepServerRunning { get; set; } = false;
            public TransportMethod TransportMethod { get; set; } = TransportMethod.streamableHttp;
            public AuthOption AuthOption { get; set; } = AuthOption.none;
            public ConnectionMode ConnectionMode { get; set; } = ConnectionMode.Custom;

            /// <summary>
            /// Optional path to the Node.js MCP server entry script (cocli's bin/server.mjs).
            /// Absolute path, or relative to the Unity project root. When null/empty the
            /// plugin auto-discovers the server (the Unity project's node_modules, then the
            /// npm global installation). When set, the path must exist or the plugin refuses
            /// to launch the server (it will still connect to an already-running one).
            /// Serialized as "nodeServerPath" in JSON.
            /// </summary>
            [JsonPropertyName("nodeServerPath")]
            public string? NodeServerPath { get; set; }

            // ── Safe-defaults (fail-closed) network gates ─────────────────────────────────
            //
            // These three flags collectively implement a fail-closed posture for newly-created
            // configs: by default the plugin refuses to bind to LAN/all-interfaces and refuses to
            // talk plaintext HTTP to a remote host, and it requires a token whenever a LAN bind
            // is enabled. Existing on-disk configs that already use a non-loopback LocalHost are
            // auto-migrated in UnityCopilotPluginEditor.GetOrCreateConfig so the new flags do NOT
            // silently break installations that were intentionally opened up before this change.
            //
            // The gates are consulted by SafeDefaultsGuard at server-start time and by the
            // UI / configurators when they emit warnings. They are NOT enforced server-side
            // (the server binary is a separate NuGet package); they gate the *plugin's*
            // willingness to start the local server with an unsafe LocalHost or to generate
            // insecure remote configurations.

            /// <summary>
            /// When false (the safe default for new configs), the plugin refuses to start the
            /// local MCP server if <see cref="LocalHost"/> resolves to a non-loopback address
            /// (e.g. <c>0.0.0.0</c>, <c>::</c>, or a LAN IP). Set to true to opt in to binding
            /// the server on every interface so other machines on the LAN can connect.
            /// </summary>
            public bool AllowLanBind { get; set; } = false;

            /// <summary>
            /// When false (the safe default), the plugin warns / refuses to emit AI-agent client
            /// configurations that point at a remote (non-loopback) URL over plain <c>http://</c>.
            /// Set to true to acknowledge the lack of TLS on a remote endpoint and proceed anyway.
            /// </summary>
            public bool AllowInsecureRemoteHttp { get; set; } = false;

            /// <summary>
            /// When true (the safe default), enabling <see cref="AllowLanBind"/> additionally
            /// requires a non-empty <see cref="Token"/> with <see cref="AuthOption"/> set to
            /// <c>required</c>. Set to false only when LAN bind needs to coexist with
            /// <c>auth=none</c> (not recommended).
            /// </summary>
            public bool ForceTokenWhenLanBind { get; set; } = true;
            public List<CopilotFeature> Tools { get; set; } = new();
            public List<CopilotFeature> Prompts { get; set; } = new();
            public List<CopilotFeature> Resources { get; set; } = new();

            /// <summary>
            /// When non-null, only the tools whose names appear in this list are enabled;
            /// all others are disabled. Set by the <c>UNITY_MCP_TOOLS</c> environment variable
            /// (comma-separated tool IDs). Not persisted to disk.
            /// </summary>
            [JsonIgnore]
            public List<string>? EnabledToolsOverride { get; set; }

            public UnityConnectionConfig()
            {
                SetDefault();
            }

            public UnityConnectionConfig SetDefault()
            {
                Host = DefaultHost;
                var isCi = EnvironmentUtils.IsCi();
                KeepConnected = !isCi;
                KeepServerRunning = !isCi;
                TransportMethod = TransportMethod.streamableHttp;
                AuthOption = AuthOption.none;
                ConnectionMode = ConnectionMode.Custom;
                NodeServerPath = null;
                LogLevel = LogLevel.Warning;
                TimeoutMs = Consts.Hub.DefaultTimeoutMs;
                Tools = DefaultTools;
                Prompts = DefaultPrompts;
                Resources = DefaultResources;
                Token = GenerateToken();
                return this;
            }

            public class CopilotFeature
            {
                public string Name { get; set; } = string.Empty;
                public bool Enabled { get; set; } = true;

                public CopilotFeature() { }
                public CopilotFeature(string name, bool enabled)
                {
                    Name = name;
                    Enabled = enabled;
                }
            }
        }
    }

    /// <summary>Local/custom server connection. The legacy Cloud mode (remote
    /// ai-game.dev endpoint) was removed in 1.0.3; configs that still carry
    /// "Cloud" are mapped to Custom on load.</summary>
    public enum ConnectionMode
    {
        Custom
    }
}
