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
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using com.AtelierAI.Uco.Framework.Common;
using com.AtelierAI.Uco.Framework.Common.Model;
using com.AtelierAI.Uco.Framework.Skills;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using R3;
using WsState = com.AtelierAI.Uco.Framework.ConnectionState;

namespace com.AtelierAI.Uco.Framework
{
    public partial class McpPlugin : IUcoPlugin, IDisposable
    {
        private readonly ILogger<McpPlugin> _logger;
        private readonly IMcpManagerHub _mcpManagerHub;
        private readonly CompositeDisposable _disposables = new();
        private readonly CancellationTokenSource _cancellationTokenSource;
        private readonly ThreadSafeBool _isDisposed = new(false);
        private readonly Common.Version _version;
        private readonly ISkillFileGenerator _skillFileGenerator;
        private readonly SkillContentCollection _skillContentCollection;
        private readonly ConnectionConfig _connectionConfig;

        public ILogger Logger => _logger;
        public IMcpManager UcoManager { get; private set; }
        public IMcpManagerHub UcoManagerHub => _mcpManagerHub;
        public Common.Version Version => _version;
        public VersionHandshakeResponse? VersionHandshakeStatus => _mcpManagerHub?.VersionHandshakeStatus;
        public ulong ToolCallsCount => UcoManager.ToolManager?.ToolCallsCount ?? 0;
        public ReadOnlyReactiveProperty<ConnectionState> ConnectionState => _mcpManagerHub?.ConnectionState
            ?? new ReactiveProperty<ConnectionState>(WsState.Disconnected);
        public ReadOnlyReactiveProperty<bool> KeepConnected => _mcpManagerHub?.KeepConnected
            ?? new ReactiveProperty<bool>(false);
        public Observable<Unit> OnAuthorizationRejected => _mcpManagerHub?.OnAuthorizationRejected
            ?? Observable.Empty<Unit>();

        public McpPlugin(
            ILogger<McpPlugin> logger,
            IMcpManager mcpManager,
            IMcpManagerHub mcpManagerHub,
            Common.Version version,
            ISkillFileGenerator skillFileGenerator,
            SkillContentCollection skillContentCollection,
            IOptions<ConnectionConfig>? connectionConfig = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _logger.LogTrace("{class} Ctor.", nameof(McpPlugin));

            UcoManager = mcpManager ?? throw new ArgumentNullException(nameof(mcpManager));
            _cancellationTokenSource = _disposables.ToCancellationTokenSource();

            _mcpManagerHub = mcpManagerHub ?? throw new ArgumentNullException(nameof(mcpManagerHub));
            _version = version ?? throw new ArgumentNullException(nameof(version));
            _connectionConfig = connectionConfig?.Value ?? new ConnectionConfig();
            _skillFileGenerator = skillFileGenerator ?? throw new ArgumentNullException(nameof(skillFileGenerator));
            _skillContentCollection = skillContentCollection ?? throw new ArgumentNullException(nameof(skillContentCollection));
            _mcpManagerHub.SetCapabilityRegistrationHandler(RegisterCapabilitiesAsync);

            UcoManager.OnForceDisconnect
                .Subscribe(_ =>
                {
                    _logger.LogDebug("{method}, force disconnect requested.",
                        nameof(UcoManager.OnForceDisconnect));

                    _mcpManagerHub.Disconnect();
                })
                .AddTo(_disposables);

            UcoManager.ToolManager?.OnToolsUpdated
                .ThrottleFirst(TimeSpan.FromMilliseconds(100))
                .Subscribe(async _ =>
                {
                    _logger.LogDebug("{method}, tools updated event received.",
                        nameof(UcoManager.ToolManager.OnToolsUpdated));

                    if (_cancellationTokenSource.Token.IsCancellationRequested)
                        return;

                    try
                    {
                        GenerateSkillFilesIfNeeded();
                    }
                    catch (InvalidOperationException ex)
                    {
                        _logger.LogError(ex,
                            "{method}: skill auto-generation skipped — host did not provide a project root. " +
                            "Set ConnectionConfig.ProjectRootPath or pass basePath to GenerateSkillFiles.",
                            nameof(UcoManager.ToolManager.OnToolsUpdated));
                    }

                    if (_mcpManagerHub == null)
                    {
                        _logger.LogWarning("{method}, RPC Router is not initialized, cannot notify about updated tools.",
                            nameof(UcoManager.ToolManager.OnToolsUpdated));
                        return;
                    }

                    // Fail-closed notify: ConnectionState reaches Connected only after
                    // transport + version handshake + capability registration complete
                    // (BaseHubConnector calls TrySetConnected last), so a notify while
                    // any other state is dispatched onto a server that cannot answer it —
                    // in a configless/fresh-install environment the RPC times out after
                    // 10s and rethrows out of this async-void subscription as an
                    // unhandled exception. Nothing is lost by skipping: every
                    // (re)connection re-registers the full tool set as part of
                    // initialization, which covers changes from the Connecting window.
                    if (_mcpManagerHub.ConnectionState.CurrentValue is not WsState.Connected)
                    {
                        _logger.LogDebug(
                            "{method}, connection state is {state}, not Connected; a pre-handshake tools update notification cannot be served. Skipping.",
                            nameof(UcoManager.ToolManager.OnToolsUpdated), _mcpManagerHub.ConnectionState.CurrentValue);
                        return;
                    }

                    try
                    {
                        await _mcpManagerHub.NotifyAboutUpdatedTools(new Common.Model.RequestToolsUpdated());
                    }
                    catch (Exception ex)
                    {
                        // Contained dispatch: this subscription is async-void, so an
                        // exception escaping the await would surface as an unhandled
                        // exception (bypassing the logger diagnostics channel entirely).
                        // Route it through the logger instead — the diagnostics channel
                        // bounds the exception detail to type + message.
                        _logger.LogError(ex,
                            "{method}: dispatching the tools update notification failed: {exceptionType}: {message}.",
                            nameof(UcoManager.ToolManager.OnToolsUpdated), ex.GetType().Name, ex.Message);
                    }
                })
                .AddTo(_disposables);

            // Generate skill files for the initial set of tools on build
            try
            {
                GenerateSkillFilesIfNeeded();
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogError(ex,
                    "{ctor}: initial skill generation skipped — host did not provide a project root. " +
                    "Set ConnectionConfig.ProjectRootPath or pass basePath to GenerateSkillFiles.",
                    nameof(McpPlugin));
            }

            UcoManager.PromptManager?.OnPromptsUpdated
                .ThrottleFirst(TimeSpan.FromMilliseconds(100))
                .Subscribe(async _ =>
                {
                    _logger.LogDebug("{method}, prompts updated event received.",
                        nameof(UcoManager.PromptManager.OnPromptsUpdated));

                    if (_cancellationTokenSource.Token.IsCancellationRequested)
                        return;

                    if (_mcpManagerHub == null)
                    {
                        _logger.LogWarning("{method}, RPC Router is not initialized, cannot notify about updated prompts.",
                            nameof(UcoManager.PromptManager.OnPromptsUpdated));
                        return;
                    }

                    // Fail-closed notify — see the tools-updated subscription above.
                    if (_mcpManagerHub.ConnectionState.CurrentValue is not WsState.Connected)
                    {
                        _logger.LogDebug(
                            "{method}, connection state is {state}, not Connected; a pre-handshake prompts update notification cannot be served. Skipping.",
                            nameof(UcoManager.PromptManager.OnPromptsUpdated), _mcpManagerHub.ConnectionState.CurrentValue);
                        return;
                    }

                    try
                    {
                        await _mcpManagerHub.NotifyAboutUpdatedPrompts(new Common.Model.RequestPromptsUpdated());
                    }
                    catch (Exception ex)
                    {
                        // Contained dispatch — see the tools-updated subscription above.
                        _logger.LogError(ex,
                            "{method}: dispatching the prompts update notification failed: {exceptionType}: {message}.",
                            nameof(UcoManager.PromptManager.OnPromptsUpdated), ex.GetType().Name, ex.Message);
                    }
                })
                .AddTo(_disposables);

            UcoManager.ResourceManager?.OnResourcesUpdated
                .ThrottleFirst(TimeSpan.FromMilliseconds(100))
                .Subscribe(async _ =>
                {
                    _logger.LogDebug("{method}, resources updated event received.",
                        nameof(UcoManager.ResourceManager.OnResourcesUpdated));

                    if (_cancellationTokenSource.Token.IsCancellationRequested)
                        return;

                    if (_mcpManagerHub == null)
                    {
                        _logger.LogWarning("{method}, RPC Router is not initialized, cannot notify about updated resources.",
                            nameof(UcoManager.ResourceManager.OnResourcesUpdated));
                        return;
                    }

                    // Fail-closed notify — see the tools-updated subscription above.
                    if (_mcpManagerHub.ConnectionState.CurrentValue is not WsState.Connected)
                    {
                        _logger.LogDebug(
                            "{method}, connection state is {state}, not Connected; a pre-handshake resources update notification cannot be served. Skipping.",
                            nameof(UcoManager.ResourceManager.OnResourcesUpdated), _mcpManagerHub.ConnectionState.CurrentValue);
                        return;
                    }

                    try
                    {
                        await _mcpManagerHub.NotifyAboutUpdatedResources(new Common.Model.RequestResourcesUpdated());
                    }
                    catch (Exception ex)
                    {
                        // Contained dispatch — see the tools-updated subscription above.
                        _logger.LogError(ex,
                            "{method}: dispatching the resources update notification failed: {exceptionType}: {message}.",
                            nameof(UcoManager.ResourceManager.OnResourcesUpdated), ex.GetType().Name, ex.Message);
                    }
                })
                .AddTo(_disposables);
        }

        private async Task RegisterCapabilitiesAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The Node bridge currently uses the tools update as the runner-eligibility
            // signal. Advertise prompts and resources first, then tools last, so that signal
            // cannot make the generation ready while another capability is still pending.
            if (UcoManager.PromptManager != null)
            {
                var prompts = await _mcpManagerHub.NotifyAboutUpdatedPrompts(
                    new Common.Model.RequestPromptsUpdated(),
                    cancellationToken);
                EnsureCapabilityRegistrationSucceeded("prompts", prompts);
            }

            if (UcoManager.ResourceManager != null)
            {
                var resources = await _mcpManagerHub.NotifyAboutUpdatedResources(
                    new Common.Model.RequestResourcesUpdated(),
                    cancellationToken);
                EnsureCapabilityRegistrationSucceeded("resources", resources);
            }

            var tools = await _mcpManagerHub.NotifyAboutUpdatedTools(
                new Common.Model.RequestToolsUpdated(),
                cancellationToken);
            EnsureCapabilityRegistrationSucceeded("tools", tools);

            _logger.LogDebug(
                "{method}, initial prompts/resources/tools registration completed.",
                nameof(RegisterCapabilitiesAsync));
        }

        private static void EnsureCapabilityRegistrationSucceeded(
            string capability,
            ResponseData response)
        {
            if (response != null && response.Status == ResponseStatus.Success)
                return;

            var detail = response?.Message;
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail)
                ? $"The server rejected {capability} capability registration."
                : $"The server rejected {capability} capability registration: {detail}");
        }

        public bool GenerateSkillFilesIfNeeded(string? path = null)
        {
            if (!_connectionConfig.GenerateSkillFiles)
                return false;

            return GenerateSkillFiles(path);
        }

        public bool GenerateSkillFiles(string? path = null)
        {
            var skillsPath = ResolveSkillsPath(path);
            var success = true;

            var tools = UcoManager.ToolManager?.GetAllTools();
            if (tools == null)
            {
                success = false;
            }
            else
            {
                var systemTools = UcoManager.SystemToolManager?.GetAllTools();
                var allTools = systemTools != null ? tools.Concat(systemTools) : tools;
                if (!_skillFileGenerator.Generate(allTools, skillsPath, _connectionConfig.Host))
                    success = false;
            }

            if (_skillContentCollection.Count > 0)
            {
                if (!_skillFileGenerator.Generate(_skillContentCollection.Values, skillsPath))
                    success = false;
            }

            return success;
        }

        public bool DeleteSkillFiles(string? path = null)
        {
            var skillsPath = ResolveSkillsPath(path);
            var success = true;

            var tools = UcoManager.ToolManager?.GetAllTools();
            if (tools == null)
            {
                success = false;
            }
            else
            {
                var systemTools = UcoManager.SystemToolManager?.GetAllTools();
                var allTools = systemTools != null ? tools.Concat(systemTools) : tools;
                if (!_skillFileGenerator.Delete(allTools, skillsPath))
                    success = false;
            }

            if (_skillContentCollection.Count > 0)
            {
                if (!_skillFileGenerator.Delete(_skillContentCollection.Values, skillsPath))
                    success = false;
            }

            return success;
        }

        private string ResolveSkillsPath(string? basePath)
        {
            var skillsPath = _connectionConfig.SkillsPath;

            if (Path.IsPathRooted(skillsPath))
                return Path.GetFullPath(skillsPath);

            var resolvedBase = basePath ?? _connectionConfig.ProjectRootPath;
            if (string.IsNullOrEmpty(resolvedBase))
            {
                throw new InvalidOperationException(
                    $"Cannot resolve relative SkillsPath '{skillsPath}': no basePath was supplied " +
                    $"and {nameof(ConnectionConfig)}.{nameof(ConnectionConfig.ProjectRootPath)} is not set. " +
                    $"Host applications must either pass an explicit basePath to GenerateSkillFiles / " +
                    $"DeleteSkillFiles, or set ConnectionConfig.ProjectRootPath at construction time. " +
                    $"Silent fallback to Environment.CurrentDirectory has been removed to prevent " +
                    $"skill files landing outside the host project (see GitHub issue #107).");
            }

            return Path.GetFullPath(Path.Combine(resolvedBase, skillsPath));
        }

        public Task<bool> Connect(CancellationToken cancellationToken = default)
        {
            if (_isDisposed.Value)
            {
                _logger.LogWarning("{method} called but already disposed, ignored.",
                    nameof(Connect));
                return Task.FromResult(false); // already disposed
            }
            _logger.LogDebug("{method} called.", nameof(Connect));
            if (_mcpManagerHub == null)
                return Task.FromResult(false);
            return _mcpManagerHub.Connect(cancellationToken);
        }

        public Task Disconnect(CancellationToken cancellationToken = default)
        {
            if (_isDisposed.Value)
            {
                _logger.LogWarning("{method} called but already disposed, ignored.",
                    nameof(Disconnect));
                return Task.CompletedTask; // already disposed
            }
            _logger.LogDebug("{method} called.", nameof(Disconnect));
            if (_mcpManagerHub == null)
                return Task.CompletedTask;
            return _mcpManagerHub.Disconnect(cancellationToken);
        }

        public void DisconnectImmediate()
        {
            if (_isDisposed.Value)
            {
                _logger.LogWarning("{method} called but already disposed, ignored.",
                    nameof(DisconnectImmediate));
                return; // already disposed
            }
            _logger.LogDebug("{method} called.", nameof(DisconnectImmediate));
            _mcpManagerHub?.DisconnectImmediate();
        }

        public void Dispose()
        {
            if (!_isDisposed.TrySetTrue())
                return; // already disposed

            _logger.LogDebug("{method} called.", nameof(Dispose));

            _disposables.Dispose();

            try
            {
                _mcpManagerHub?.DisconnectImmediate();
            }
            catch (Exception ex)
            {
                _logger.LogError("Error during async disposal: {message}\n{stackTrace}", ex.Message, ex.StackTrace);
            }

            try
            {
                _mcpManagerHub?.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogError("Error during async disposal: {message}\n{stackTrace}", ex.Message, ex.StackTrace);
            }

            UcoManager.Dispose();

            _logger.LogDebug("{method} completed.", nameof(Dispose));
        }

        ~McpPlugin() => Dispose();
    }
}
