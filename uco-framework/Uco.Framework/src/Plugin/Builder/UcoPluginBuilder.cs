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
using System.IO;
using System.Linq;
using System.Reflection;
using com.AtelierAI.Uco.Framework.Common.Hub.Client;
using com.AtelierAI.Uco.Framework.Common.Model;
using com.AtelierAI.Uco.Framework.Skills;
using com.IvanMurzak.ReflectorNet;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Version = com.AtelierAI.Uco.Framework.Common.Version;

namespace com.AtelierAI.Uco.Framework
{
    public partial class UcoBuilder : IUcoPluginBuilder
    {
        protected readonly ILogger? _logger;
        protected readonly ILoggerProvider? _loggerProvider;
        protected readonly IServiceCollection _services;

        protected readonly List<ToolMethodData> _toolMethods = new();
        protected readonly Dictionary<string, IRunTool> _toolRunners = new();

        protected readonly List<PromptMethodData> _promptMethods = new();
        protected readonly Dictionary<string, IRunPrompt> _promptRunners = new();

        protected readonly List<ResourceMethodData> _resourceMethods = new();
        protected readonly Dictionary<string, IRunResource> _resourceRunners = new();

        protected readonly List<SkillMemberData> _skillFields = new();

        // Ignore configuration for filtering assemblies, namespaces, and types
        protected readonly UcoBuilderIgnoreConfig _ignoreConfig = new();

        // Optional externally provided config instance (set via SetConfig)
        protected ConnectionConfig? _externalConfig;

        // Lazy assembly scanning - store assemblies to scan later
        protected readonly List<Assembly> _toolAssemblies = new();
        protected readonly List<Assembly> _promptAssemblies = new();
        protected readonly List<Assembly> _resourceAssemblies = new();
        protected readonly List<Assembly> _skillAssemblies = new();

        // Lazy type scanning - store types to scan later
        protected readonly List<Type> _toolTypes = new();
        protected readonly List<Type> _promptTypes = new();
        protected readonly List<Type> _resourceTypes = new();
        protected readonly List<Type> _skillTypes = new();

        protected bool isBuilt = false;
        protected bool _skillFileGeneratorSet = false;

        public IServiceCollection Services => _services;
        public ServiceProvider? ServiceProvider { get; private set; }

        public UcoBuilder(Version version, ILoggerProvider? loggerProvider = null, IServiceCollection? services = null)
        {
            _loggerProvider = loggerProvider;
            _logger = loggerProvider?.CreateLogger(nameof(UcoBuilder));
            _services = services ?? new ServiceCollection();

            if (_loggerProvider != null)
            {
                _services.AddLogging(builder => builder.AddProvider(_loggerProvider));
            }
            else
            {
                _services.AddLogging();
            }
            _services.AddSingleton(version);
            _services.AddSingleton<IConnectionManager, ConnectionManager>();
            _services.AddSingleton<IWebSocketConnectionProvider, WebSocketConnectionProvider>();

            _services.AddSingleton<IToolManager, UcoToolManager>();
            _services.AddSingleton<IPromptManager, UcoPromptManager>();
            _services.AddSingleton<IResourceManager, UcoResourceManager>();

            _services.AddSingleton<UcoSystemToolManager>();
            _services.AddSingleton<ISystemToolManager>(sp => sp.GetRequiredService<UcoSystemToolManager>());

            _services.AddSingleton<IUcoPlugin, UcoPlugin>();
            _services.AddSingleton<IPluginManagerHub, UcoManagerClientHub>();

            _services.AddSingleton<UcoManager>();
            _services.AddSingleton<IPluginManager>(sp => sp.GetRequiredService<UcoManager>());
            _services.AddSingleton<IClientPluginManager>(sp => sp.GetRequiredService<UcoManager>());

            _services.AddSingleton<ISkillFileGenerator, SkillFileGenerator>();

            // Authoring safety is the outermost policy middleware. Register it
            // before caller-provided middleware so no custom layer can perform
            // an authoring side effect before policy approval. The default root
            // follows the host's current project directory; Unity's builder
            // supplies Application.dataPath's parent explicitly.
            _services.TryAddSingleton<ProjectPathPolicy>(_ =>
                new ProjectPathPolicy(Directory.GetCurrentDirectory()));
            _services.TryAddSingleton<AuthoringSafetyPolicy>(sp =>
                new AuthoringSafetyPolicy(sp.GetRequiredService<ProjectPathPolicy>()));
            _services.TryAddSingleton<IToolExecutionScheduler, InlineToolExecutionScheduler>();
            _services.AddSingleton<IToolExecutionMiddleware>(sp =>
                new AuthoringSafetyMiddleware(
                    sp.GetRequiredService<AuthoringSafetyPolicy>(),
                    sp.GetRequiredService<IToolExecutionScheduler>()));

            // Register the concrete pipeline once.  The pass-through middleware
            // is appended during Build so custom middleware registered through
            // the additive APIs retains its registration order and wraps the
            // default behavior.
            _services.AddSingleton<ToolExecutionPipeline>(sp =>
                new ToolExecutionPipeline(sp.GetServices<IToolExecutionMiddleware>()));
        }

        #region Tool
        public virtual IUcoPluginBuilder WithTool(Type classType, MethodInfo methodInfo)
        {
            ThrowIfBuilt();

            var attribute = methodInfo.GetCustomAttribute<UcoToolAttribute>();
            return WithTool(attribute!, classType, methodInfo);
        }
        public virtual IUcoPluginBuilder WithTool(string name, string? title, Type classType, MethodInfo methodInfo)
        {
            ThrowIfBuilt();

            var attribute = new UcoToolAttribute(name, title);
            return WithTool(attribute, classType, methodInfo);
        }
        public virtual IUcoPluginBuilder WithTool(UcoToolAttribute attribute, Type classType, MethodInfo methodInfo)
        {
            ThrowIfBuilt();

            if (attribute == null)
            {
                _logger?.LogWarning($"Method {classType.FullName}{methodInfo.Name} does not have a '{nameof(UcoToolAttribute)}'.");
                return this;
            }

            if (string.IsNullOrEmpty(attribute.Name))
                throw new ArgumentException($"Tool name cannot be null or empty. Type: {classType.Name}, Method: {methodInfo.Name}");

            _toolMethods.Add(new ToolMethodData
            (
                classType: classType,
                methodInfo: methodInfo,
                attribute: attribute
            ));
            return this;
        }
        public virtual IUcoPluginBuilder AddTool(string name, IRunTool runner)
        {
            ThrowIfBuilt();

            if (_toolRunners.ContainsKey(name))
                throw new ArgumentException($"Tool with name '{name}' already exists.");

            _toolRunners.Add(name, GuardedRunTool.Wrap(runner));
            return this;
        }

        public virtual IUcoPluginBuilder AddToolExecutionMiddleware<T>()
            where T : class, IToolExecutionMiddleware
        {
            ThrowIfBuilt();
            _services.AddSingleton<IToolExecutionMiddleware, T>();
            return this;
        }

        public virtual IUcoPluginBuilder AddToolExecutionMiddleware(IToolExecutionMiddleware middleware)
        {
            ThrowIfBuilt();
            if (middleware == null)
                throw new ArgumentNullException(nameof(middleware));

            _services.AddSingleton<IToolExecutionMiddleware>(middleware);
            return this;
        }

        public virtual IUcoPluginBuilder WithProjectPathPolicy(ProjectPathPolicy policy)
        {
            ThrowIfBuilt();
            if (policy == null)
                throw new ArgumentNullException(nameof(policy));

            _services.AddSingleton(policy);
            return this;
        }

        public virtual IUcoPluginBuilder WithHandshakeIdentity(com.AtelierAI.Uco.Framework.Common.IHandshakeIdentity identity)
        {
            ThrowIfBuilt();
            if (identity == null)
                throw new ArgumentNullException(nameof(identity));

            _services.AddSingleton(identity);
            return this;
        }

        public virtual IUcoPluginBuilder WithToolExecutionScheduler(IToolExecutionScheduler scheduler)
        {
            ThrowIfBuilt();
            if (scheduler == null)
                throw new ArgumentNullException(nameof(scheduler));

            _services.Replace(
                ServiceDescriptor.Singleton<IToolExecutionScheduler>(scheduler));
            return this;
        }
        #endregion

        #region Prompt
        public virtual IUcoPluginBuilder WithPrompt(string name, Type classType, MethodInfo methodInfo)
        {
            ThrowIfBuilt();

            var attribute = methodInfo.GetCustomAttribute<UcoPromptAttribute>();
            if (attribute == null)
            {
                _logger?.LogWarning($"Method {classType.FullName}{methodInfo.Name} does not have a '{nameof(UcoPromptAttribute)}'.");
                return this;
            }

            if (string.IsNullOrEmpty(attribute.Name))
                throw new ArgumentException($"Prompt name cannot be null or empty. Type: {classType.Name}, Method: {methodInfo.Name}");

            _promptMethods.Add(new PromptMethodData
            (
                classType: classType,
                methodInfo: methodInfo,
                attribute: attribute
            ));
            return this;
        }
        public virtual IUcoPluginBuilder AddPrompt(string name, IRunPrompt runner)
        {
            ThrowIfBuilt();

            if (_promptRunners.ContainsKey(name))
                throw new ArgumentException($"Prompt with name '{name}' already exists.");

            _promptRunners.Add(name, runner);
            return this;
        }
        #endregion

        #region Resource
        public virtual IUcoPluginBuilder WithResource(Type classType, MethodInfo getContentMethod)
        {
            ThrowIfBuilt();

            var attribute = getContentMethod.GetCustomAttribute<UcoResourceAttribute>();
            if (attribute == null)
            {
                _logger?.LogWarning($"Method {classType.FullName}{getContentMethod.Name} does not have a '{nameof(UcoResourceAttribute)}'.");
                return this;
            }

            var listResourcesMethodName = attribute.ListResources ?? throw new InvalidOperationException($"Method {getContentMethod.Name} does not have a 'ListResources'.");
            var listResourcesMethod = classType.GetMethod(listResourcesMethodName);
            if (listResourcesMethod == null)
                throw new InvalidOperationException($"Method {classType.FullName}{listResourcesMethodName} not found in type {classType.Name}.");

            if (!getContentMethod.ReturnType.IsArray ||
                !typeof(ResponseResourceContent).IsAssignableFrom(getContentMethod.ReturnType.GetElementType()))
                throw new InvalidOperationException($"Method {classType.FullName}{getContentMethod.Name} must return {nameof(ResponseResourceContent)} array.");

            if (!listResourcesMethod.ReturnType.IsArray ||
                !typeof(ResponseListResource).IsAssignableFrom(listResourcesMethod.ReturnType.GetElementType()))
                throw new InvalidOperationException($"Method {classType.FullName}{listResourcesMethod.Name} must return {nameof(ResponseListResource)} array.");

            _resourceMethods.Add(new ResourceMethodData
            (
                classType: classType,
                attribute: attribute,
                getContentMethod: getContentMethod,
                listResourcesMethod: listResourcesMethod
            ));

            return this;
        }
        public virtual IUcoPluginBuilder AddResource(IRunResource resourceParams)
        {
            ThrowIfBuilt();

            if (_resourceRunners == null)
                throw new ArgumentNullException(nameof(_resourceRunners));

            if (resourceParams == null)
                throw new ArgumentNullException(nameof(resourceParams));

            if (_resourceRunners.ContainsKey(resourceParams.Route))
                throw new ArgumentException($"Resource with routing '{resourceParams.Route}' already exists.");

            _resourceRunners.Add(resourceParams.Route, resourceParams);
            return this;
        }
        #endregion

        #region Other
        public virtual IUcoPluginBuilder AddLogging(Action<ILoggingBuilder> loggingBuilder)
        {
            ThrowIfBuilt();

            _services.AddLogging(loggingBuilder);
            return this;
        }

        public virtual IUcoPluginBuilder SetConfig(ConnectionConfig config)
        {
            ThrowIfBuilt();

            _externalConfig = config ?? throw new ArgumentNullException(nameof(config));
            return this;
        }

        public virtual IUcoPluginBuilder WithConfig(Action<ConnectionConfig> config)
        {
            ThrowIfBuilt();

            if (_externalConfig != null)
                config(_externalConfig);
            else
                _services.Configure(config);
            return this;
        }

        public virtual IUcoPluginBuilder WithSkillFileGenerator<T>()
            where T : class, ISkillFileGenerator
        {
            ThrowIfBuilt();
            ThrowIfSkillFileGeneratorSet();

            _skillFileGeneratorSet = true;
            _services.AddSingleton<ISkillFileGenerator, T>();
            return this;
        }

        public virtual IUcoPluginBuilder WithSkillFileGenerator(ISkillFileGenerator instance)
        {
            ThrowIfBuilt();
            ThrowIfSkillFileGeneratorSet();

            if (instance == null)
                throw new ArgumentNullException(nameof(instance));

            _skillFileGeneratorSet = true;
            _services.AddSingleton<ISkillFileGenerator>(instance);
            return this;
        }

        public virtual IUcoPluginBuilder WithConfigFromArgsOrEnv(string[]? args = null) => WithConfig(config =>
        {
            config.Host = ConnectionConfig.GetEndpointFromArgsOrEnv(args);
            config.Token = ConnectionConfig.GetTokenFromArgsOrEnv(args);
            config.TimeoutMs = ConnectionConfig.GetTimeoutFromArgsOrEnv(args);
        });

        /// <summary>
        /// Builds the plugin instance. This is a one-time operation - once Build() is called, the builder cannot be modified or built again.
        /// </summary>
        /// <param name="reflector">The reflector instance used for reflection operations.</param>
        /// <returns>The built plugin instance.</returns>
        /// <exception cref="ArgumentNullException">Thrown if the reflector is null.</exception>
        public virtual IUcoPlugin Build(Reflector reflector)
        {
            ThrowIfBuilt();

            if (reflector == null)
                throw new ArgumentNullException(nameof(reflector));

            // Process all assemblies with caching optimization
            ProcessAllAssemblies();

            _services.AddSingleton(reflector);

            var standardMethods = _toolMethods.Where(m => m.Attribute.ToolType == UcoToolType.Standard).ToList();
            var systemMethods = _toolMethods.Where(m => m.Attribute.ToolType == UcoToolType.System).ToList();

            var standardRunners = _toolRunners.Where(r => r.Value.ToolType == UcoToolType.Standard).ToDictionary(r => r.Key, r => r.Value);
            var systemRunners = _toolRunners.Where(r => r.Value.ToolType == UcoToolType.System).ToDictionary(r => r.Key, r => r.Value);

            _services.AddSingleton(new ToolRunnerCollection(reflector, _loggerProvider?.CreateLogger(nameof(ToolRunnerCollection)))
                .Add(standardMethods)
                .Add(standardRunners));

            _services.AddSingleton(new SystemToolRunnerCollection(reflector, _loggerProvider?.CreateLogger(nameof(SystemToolRunnerCollection)))
                .Add(systemMethods)
                .Add(systemRunners));

            _services.AddSingleton(new PromptRunnerCollection(reflector, _loggerProvider?.CreateLogger(nameof(PromptRunnerCollection)))
                .Add(_promptMethods)
                .Add(_promptRunners));

            _services.AddSingleton(new ResourceRunnerCollection(reflector, _loggerProvider?.CreateLogger(nameof(ResourceRunnerCollection)))
                .Add(_resourceMethods)
                .Add(_resourceRunners));

            _services.AddSingleton(new SkillContentCollection(_loggerProvider?.CreateLogger(nameof(SkillContentCollection)))
                .Add(_skillFields));

            // The pass-through implementation is deliberately registered last:
            // custom middleware runs in the exact order in which callers added
            // it, while an empty builder still has a concrete default.
            _services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IToolExecutionMiddleware, PassThroughToolExecutionMiddleware>());

            if (_externalConfig != null)
                _services.AddSingleton<IOptions<ConnectionConfig>>(new OptionsWrapper<ConnectionConfig>(_externalConfig));

            ServiceProvider = _services.BuildServiceProvider();
            isBuilt = true;

            return ServiceProvider.GetRequiredService<IUcoPlugin>();
        }

        protected virtual void ThrowIfBuilt()
        {
            if (isBuilt)
                throw new InvalidOperationException("The builder has already been built.");
        }

        protected virtual void ThrowIfSkillFileGeneratorSet()
        {
            if (_skillFileGeneratorSet)
                throw new InvalidOperationException($"{nameof(ISkillFileGenerator)} has already been set. Only one {nameof(ISkillFileGenerator)} can be registered.");
        }
        #endregion
    }
}
