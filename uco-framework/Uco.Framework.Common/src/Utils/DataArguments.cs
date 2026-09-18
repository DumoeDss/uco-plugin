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

namespace com.AtelierAI.Uco.Framework.Common.Utils
{
    public interface IDataArguments
    {
        int Port { get; }
        int PluginTimeoutMs { get; }
        int IdleTimeoutSeconds { get; }
        Consts.Uco.Server.AuthOption Authorization { get; }
        string? Token { get; }

        // Webhook configuration
        string? WebhookToolUrl { get; }
        string? WebhookPromptUrl { get; }
        string? WebhookResourceUrl { get; }
        string? WebhookConnectionUrl { get; }
        string? WebhookToken { get; }
        string? WebhookHeader { get; }
        int WebhookTimeoutMs { get; }

        // Authorization webhook configuration
        string? WebhookAuthorizationUrl { get; }
        bool WebhookAuthorizationFailOpen { get; }
    }
    public class DataArguments : IDataArguments
    {
        public int Port { get; private set; } = 8080;
        public int PluginTimeoutMs { get; private set; }
        public int IdleTimeoutSeconds { get; private set; } = Consts.Uco.Server.DefaultIdleTimeoutSeconds;
        public Consts.Uco.Server.AuthOption Authorization { get; private set; } = Consts.Uco.Server.AuthOption.none;
        public string? Token { get; private set; }

        // Webhook configuration
        public string? WebhookToolUrl { get; private set; }
        public string? WebhookPromptUrl { get; private set; }
        public string? WebhookResourceUrl { get; private set; }
        public string? WebhookConnectionUrl { get; private set; }
        public string? WebhookToken { get; private set; }
        public string? WebhookHeader { get; private set; }
        public int WebhookTimeoutMs { get; private set; } = 10000;

        // Authorization webhook configuration
        public string? WebhookAuthorizationUrl { get; private set; }
        public bool WebhookAuthorizationFailOpen { get; private set; } = false;

        public DataArguments(string[] args)
        {
            Port = Consts.Hub.DefaultPort;
            PluginTimeoutMs = Consts.Hub.DefaultTimeoutMs;

            ParseEnvironmentVariables(); // env variables - second priority
            ParseCommandLineArguments(args); // command line args - first priority (override previous values)

            // Default to 'none' authorization if not explicitly set
            if (Authorization == Consts.Uco.Server.AuthOption.unknown)
                Authorization = Consts.Uco.Server.AuthOption.none;
        }

        void ParseEnvironmentVariables()
        {
            // --- Global variables ---

            var envPort = Environment.GetEnvironmentVariable(Consts.Uco.Server.Env.Port);
            if (envPort != null && int.TryParse(envPort, out var parsedEnvPort))
                Port = parsedEnvPort;

            // --- Plugin variables ---

            var envPluginTimeout = Environment.GetEnvironmentVariable(Consts.Uco.Server.Env.PluginTimeout);
            if (envPluginTimeout != null && int.TryParse(envPluginTimeout, out var parsedEnvTimeoutMs))
                PluginTimeoutMs = parsedEnvTimeoutMs;

            var envIdleTimeoutSeconds = Environment.GetEnvironmentVariable(Consts.Uco.Server.Env.IdleTimeoutSeconds);
            if (envIdleTimeoutSeconds != null && int.TryParse(envIdleTimeoutSeconds, out var parsedEnvIdleTimeoutSeconds) && parsedEnvIdleTimeoutSeconds > 0)
                IdleTimeoutSeconds = parsedEnvIdleTimeoutSeconds;

            // --- Token ---

            var envToken = Environment.GetEnvironmentVariable(Consts.Uco.Server.Env.Token);
            if (envToken != null)
                Token = envToken;

            // --- Deployment mode ---

            var envDeployment = Environment.GetEnvironmentVariable(Consts.Uco.Server.Env.Authorization);
            if (envDeployment != null && Enum.TryParse(envDeployment, true, out Consts.Uco.Server.AuthOption parsedEnvDeployment))
                Authorization = parsedEnvDeployment;

            // --- Webhook variables ---

            var envWebhookToolUrl = Environment.GetEnvironmentVariable(Consts.Uco.Server.Env.WebhookToolUrl);
            if (envWebhookToolUrl != null)
                WebhookToolUrl = envWebhookToolUrl;

            var envWebhookPromptUrl = Environment.GetEnvironmentVariable(Consts.Uco.Server.Env.WebhookPromptUrl);
            if (envWebhookPromptUrl != null)
                WebhookPromptUrl = envWebhookPromptUrl;

            var envWebhookResourceUrl = Environment.GetEnvironmentVariable(Consts.Uco.Server.Env.WebhookResourceUrl);
            if (envWebhookResourceUrl != null)
                WebhookResourceUrl = envWebhookResourceUrl;

            var envWebhookConnectionUrl = Environment.GetEnvironmentVariable(Consts.Uco.Server.Env.WebhookConnectionUrl);
            if (envWebhookConnectionUrl != null)
                WebhookConnectionUrl = envWebhookConnectionUrl;

            var envWebhookToken = Environment.GetEnvironmentVariable(Consts.Uco.Server.Env.WebhookToken);
            if (envWebhookToken != null)
                WebhookToken = envWebhookToken;

            var envWebhookHeader = Environment.GetEnvironmentVariable(Consts.Uco.Server.Env.WebhookHeader);
            if (envWebhookHeader != null)
                WebhookHeader = envWebhookHeader;

            var envWebhookTimeout = Environment.GetEnvironmentVariable(Consts.Uco.Server.Env.WebhookTimeout);
            if (envWebhookTimeout != null && int.TryParse(envWebhookTimeout, out var parsedEnvWebhookTimeout))
                WebhookTimeoutMs = parsedEnvWebhookTimeout;

            // --- Authorization webhook variables ---

            var envWebhookAuthorizationUrl = Environment.GetEnvironmentVariable(Consts.Uco.Server.Env.WebhookAuthorizationUrl);
            if (envWebhookAuthorizationUrl != null)
                WebhookAuthorizationUrl = envWebhookAuthorizationUrl;

            var envWebhookAuthorizationFailOpen = Environment.GetEnvironmentVariable(Consts.Uco.Server.Env.WebhookAuthorizationFailOpen);
            if (envWebhookAuthorizationFailOpen != null && bool.TryParse(envWebhookAuthorizationFailOpen, out var parsedEnvWebhookAuthorizationFailOpen))
                WebhookAuthorizationFailOpen = parsedEnvWebhookAuthorizationFailOpen;
        }
        void ParseCommandLineArguments(string[] args)
        {
            var commandLineArgs = ArgsUtils.ParseLineArguments(args);

            // --- Global variables ---

            var argPort = commandLineArgs.GetValueOrDefault(Consts.Uco.Server.Args.Port.TrimStart('-'));
            if (argPort != null && int.TryParse(argPort, out var port))
                Port = port;

            // --- Plugin variables ---

            var argPluginTimeout = commandLineArgs.GetValueOrDefault(Consts.Uco.Server.Args.PluginTimeout.TrimStart('-'));
            if (argPluginTimeout != null && int.TryParse(argPluginTimeout, out var timeoutMs))
                PluginTimeoutMs = timeoutMs;

            var argIdleTimeoutSeconds = commandLineArgs.GetValueOrDefault(Consts.Uco.Server.Args.IdleTimeoutSeconds.TrimStart('-'));
            if (argIdleTimeoutSeconds != null && int.TryParse(argIdleTimeoutSeconds, out var parsedArgIdleTimeoutSeconds) && parsedArgIdleTimeoutSeconds > 0)
                IdleTimeoutSeconds = parsedArgIdleTimeoutSeconds;

            // --- Token ---

            var argToken = commandLineArgs.GetValueOrDefault(Consts.Uco.Server.Args.Token.TrimStart('-'));
            if (argToken != null)
                Token = argToken;

            // --- Deployment mode ---

            var argDeployment = commandLineArgs.GetValueOrDefault(Consts.Uco.Server.Args.Authorization.TrimStart('-'));
            if (argDeployment != null && Enum.TryParse(argDeployment, true, out Consts.Uco.Server.AuthOption parsedArgDeployment))
                Authorization = parsedArgDeployment;

            // --- Webhook variables ---

            var argWebhookToolUrl = commandLineArgs.GetValueOrDefault(Consts.Uco.Server.Args.WebhookToolUrl.TrimStart('-'));
            if (argWebhookToolUrl != null)
                WebhookToolUrl = argWebhookToolUrl;

            var argWebhookPromptUrl = commandLineArgs.GetValueOrDefault(Consts.Uco.Server.Args.WebhookPromptUrl.TrimStart('-'));
            if (argWebhookPromptUrl != null)
                WebhookPromptUrl = argWebhookPromptUrl;

            var argWebhookResourceUrl = commandLineArgs.GetValueOrDefault(Consts.Uco.Server.Args.WebhookResourceUrl.TrimStart('-'));
            if (argWebhookResourceUrl != null)
                WebhookResourceUrl = argWebhookResourceUrl;

            var argWebhookConnectionUrl = commandLineArgs.GetValueOrDefault(Consts.Uco.Server.Args.WebhookConnectionUrl.TrimStart('-'));
            if (argWebhookConnectionUrl != null)
                WebhookConnectionUrl = argWebhookConnectionUrl;

            var argWebhookToken = commandLineArgs.GetValueOrDefault(Consts.Uco.Server.Args.WebhookToken.TrimStart('-'));
            if (argWebhookToken != null)
                WebhookToken = argWebhookToken;

            var argWebhookHeader = commandLineArgs.GetValueOrDefault(Consts.Uco.Server.Args.WebhookHeader.TrimStart('-'));
            if (argWebhookHeader != null)
                WebhookHeader = argWebhookHeader;

            var argWebhookTimeout = commandLineArgs.GetValueOrDefault(Consts.Uco.Server.Args.WebhookTimeout.TrimStart('-'));
            if (argWebhookTimeout != null && int.TryParse(argWebhookTimeout, out var parsedArgWebhookTimeout))
                WebhookTimeoutMs = parsedArgWebhookTimeout;

            // --- Authorization webhook variables ---

            var argWebhookAuthorizationUrl = commandLineArgs.GetValueOrDefault(Consts.Uco.Server.Args.WebhookAuthorizationUrl.TrimStart('-'));
            if (argWebhookAuthorizationUrl != null)
                WebhookAuthorizationUrl = argWebhookAuthorizationUrl;

            var argWebhookAuthorizationFailOpen = commandLineArgs.GetValueOrDefault(Consts.Uco.Server.Args.WebhookAuthorizationFailOpen.TrimStart('-'));
            if (argWebhookAuthorizationFailOpen != null && bool.TryParse(argWebhookAuthorizationFailOpen, out var parsedArgWebhookAuthorizationFailOpen))
                WebhookAuthorizationFailOpen = parsedArgWebhookAuthorizationFailOpen;
        }
    }
}
