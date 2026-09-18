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
using System.Reflection;
using com.AtelierAI.Uco.Framework.Skills;
using com.IvanMurzak.ReflectorNet;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace com.AtelierAI.Uco.Framework
{
    public interface IUcoPluginBuilder
    {
        IServiceCollection Services { get; }

        // Tool methods
        IUcoPluginBuilder WithTool(Type classType, MethodInfo methodInfo);
        IUcoPluginBuilder WithTool(UcoToolAttribute attribute, Type classType, MethodInfo methodInfo);
        IUcoPluginBuilder WithTool(string name, string? title, Type classType, MethodInfo methodInfo);
        IUcoPluginBuilder AddTool(string name, IRunTool runner);
        IUcoPluginBuilder WithTools<T>();
        IUcoPluginBuilder WithTools(params Type[] targetTypes);
        IUcoPluginBuilder WithTools(IEnumerable<Type> targetTypes);
        IUcoPluginBuilder WithTools(Type classType);
        IUcoPluginBuilder WithToolsFromAssembly(IEnumerable<Assembly> assemblies);
        IUcoPluginBuilder WithToolsFromAssembly(Assembly? assembly = null);

        // Tool execution middleware
        IUcoPluginBuilder AddToolExecutionMiddleware<T>()
            where T : class, IToolExecutionMiddleware;
        IUcoPluginBuilder AddToolExecutionMiddleware(IToolExecutionMiddleware middleware);
        IUcoPluginBuilder WithProjectPathPolicy(ProjectPathPolicy policy);
        IUcoPluginBuilder WithHandshakeIdentity(com.AtelierAI.Uco.Framework.Common.IHandshakeIdentity identity);
        IUcoPluginBuilder WithToolExecutionScheduler(IToolExecutionScheduler scheduler);

        // Prompt methods
        IUcoPluginBuilder WithPrompt(string name, Type classType, MethodInfo methodInfo);
        IUcoPluginBuilder AddPrompt(string name, IRunPrompt runner);
        IUcoPluginBuilder WithPrompts<T>();
        IUcoPluginBuilder WithPrompts(params Type[] targetTypes);
        IUcoPluginBuilder WithPrompts(IEnumerable<Type> targetTypes);
        IUcoPluginBuilder WithPrompts(Type classType);
        IUcoPluginBuilder WithPromptsFromAssembly(IEnumerable<Assembly> assemblies);
        IUcoPluginBuilder WithPromptsFromAssembly(Assembly? assembly = null);

        // Resource methods
        IUcoPluginBuilder WithResource(Type classType, MethodInfo getContentMethod);
        IUcoPluginBuilder AddResource(IRunResource resourceParams);
        IUcoPluginBuilder WithResources<T>();
        IUcoPluginBuilder WithResources(params Type[] targetTypes);
        IUcoPluginBuilder WithResources(IEnumerable<Type> targetTypes);
        IUcoPluginBuilder WithResources(Type classType);
        IUcoPluginBuilder WithResourcesFromAssembly(IEnumerable<Assembly> assemblies);
        IUcoPluginBuilder WithResourcesFromAssembly(Assembly? assembly = null);

        // Skill methods
        IUcoPluginBuilder WithSkills<T>();
        IUcoPluginBuilder WithSkills(params Type[] targetTypes);
        IUcoPluginBuilder WithSkills(IEnumerable<Type> targetTypes);
        IUcoPluginBuilder WithSkills(Type classType);
        IUcoPluginBuilder WithSkillsFromAssembly(IEnumerable<Assembly> assemblies);
        IUcoPluginBuilder WithSkillsFromAssembly(Assembly? assembly = null);

        // Configuration methods
        IUcoPluginBuilder AddLogging(Action<ILoggingBuilder> loggingBuilder);
        IUcoPluginBuilder SetConfig(ConnectionConfig config);
        IUcoPluginBuilder WithConfig(Action<ConnectionConfig> config);
        IUcoPluginBuilder WithConfigFromArgsOrEnv(string[]? args = null);
        IUcoPluginBuilder WithSkillFileGenerator<T>() where T : class, ISkillFileGenerator;
        IUcoPluginBuilder WithSkillFileGenerator(ISkillFileGenerator instance);
        IUcoPlugin Build(Reflector reflector);

        // Ignore Assembly methods
        IUcoPluginBuilder IgnoreAssembly(Assembly assembly);
        IUcoPluginBuilder IgnoreAssembly(string assemblyName);
        IUcoPluginBuilder IgnoreAssemblies(IEnumerable<Assembly> assemblies);
        IUcoPluginBuilder IgnoreAssemblies(params string[] assemblyNames);

        // Ignore Namespace methods
        IUcoPluginBuilder IgnoreNamespace(string namespaceName);
        IUcoPluginBuilder IgnoreNamespaces(params string[] namespaceNames);

        // Remove Ignored Assembly methods
        IUcoPluginBuilder RemoveIgnoredAssembly(Assembly assembly);
        IUcoPluginBuilder RemoveIgnoredAssembly(string assemblyName);
        IUcoPluginBuilder RemoveIgnoredAssemblies(IEnumerable<Assembly> assemblies);
        IUcoPluginBuilder RemoveIgnoredAssemblies(params string[] assemblyNames);

        // Remove Ignored Namespace methods
        IUcoPluginBuilder RemoveIgnoredNamespace(string namespaceName);
        IUcoPluginBuilder RemoveIgnoredNamespaces(params string[] namespaceNames);

        // Ignore counters
        int GetIgnoredAssembliesCount();
        int GetIgnoredTypesCount();

        // Clear Ignored methods
        IUcoPluginBuilder ClearIgnoredAssemblies();
        IUcoPluginBuilder ClearIgnoredNamespaces();
        IUcoPluginBuilder ClearAllIgnored();
    }
}
