/*
┌────────────────────────────────────────────────────────────────────────┐
│  Author: Ivan Murzak (https://github.com/IvanMurzak)                   │
│  Repository: GitHub (https://github.com/IvanMurzak/MCP-Plugin-dotnet)  │
│  Copyright (c) 2025 Ivan Murzak                                        │
│  Licensed under the Apache License, Version 2.0.                       │
│  See the LICENSE file in the project root for more information.        │
└────────────────────────────────────────────────────────────────────────┘
*/

using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Tasks;
using com.AtelierAI.Uco.Framework.Common.Model;
using com.AtelierAI.Uco.Framework.Tests.Data.Annotations;
using com.AtelierAI.Uco.Framework.Tests.Infrastructure;
using com.IvanMurzak.ReflectorNet;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;
using Xunit.Abstractions;
using Version = com.AtelierAI.Uco.Framework.Common.Version;

namespace com.AtelierAI.Uco.Framework.Tests.Managers
{
    [Collection("UcoPlugin")]
    public class UcoBuilderTests_SystemTools
    {
        private readonly ITestOutputHelper _output;
        private readonly XunitTestOutputLoggerProvider _loggerProvider;
        private readonly Version _version = new Version();

        public UcoBuilderTests_SystemTools(ITestOutputHelper output)
        {
            _output = output;
            _loggerProvider = new XunitTestOutputLoggerProvider(output);
        }

        private IUcoPlugin BuildWithMixedTools()
        {
            var reflector = new Reflector();
            var builder = new UcoBuilder(_version, _loggerProvider)
                .AddLogging(b => b.AddXunitTestOutput(_output))
                .WithTools(typeof(MixedToolTypeClass));
            return builder.Build(reflector);
        }

        // ── ToolType attribute defaults ──────────────────────────────────

        [Fact]
        public void ToolAttribute_DefaultToolType_ShouldBeStandard()
        {
            var attr = new UcoToolAttribute("test");
            attr.ToolType.ShouldBe(UcoToolType.Standard);
        }

        [Fact]
        public void ToolAttribute_ToolTypeSystem_ShouldBeSystem()
        {
            var attr = new UcoToolAttribute("test") { ToolType = UcoToolType.System };
            attr.ToolType.ShouldBe(UcoToolType.System);
        }

        // ── Builder splits standard vs system tools ─────────────────────

        [Fact]
        public void Build_StandardTools_ShouldBeInToolManager()
        {
            var plugin = BuildWithMixedTools();
            var toolManager = plugin.UcoManager.ToolManager!;
            var tools = toolManager.GetAllTools().Select(t => t.Name).ToList();

            tools.ShouldContain("standard-tool-a");
            tools.ShouldContain("standard-tool-b");
            tools.ShouldContain("standard-default");
        }

        [Fact]
        public void Build_SystemTools_ShouldNotBeInToolManager()
        {
            var plugin = BuildWithMixedTools();
            var toolManager = plugin.UcoManager.ToolManager!;
            var tools = toolManager.GetAllTools().Select(t => t.Name).ToList();

            tools.ShouldNotContain("system-tool-x");
            tools.ShouldNotContain("system-tool-y");
        }

        [Fact]
        public void Build_SystemTools_ShouldBeInSystemToolManager()
        {
            var plugin = BuildWithMixedTools();
            var systemToolManager = plugin.UcoManager.SystemToolManager!;

            systemToolManager.ShouldNotBeNull();
            systemToolManager.HasTool("system-tool-x").ShouldBeTrue();
            systemToolManager.HasTool("system-tool-y").ShouldBeTrue();
        }

        [Fact]
        public void Build_StandardTools_ShouldNotBeInSystemToolManager()
        {
            var plugin = BuildWithMixedTools();
            var systemToolManager = plugin.UcoManager.SystemToolManager!;

            systemToolManager.HasTool("standard-tool-a").ShouldBeFalse();
            systemToolManager.HasTool("standard-tool-b").ShouldBeFalse();
            systemToolManager.HasTool("standard-default").ShouldBeFalse();
        }

        [Fact]
        public void Build_ToolCounts_ShouldBeCorrect()
        {
            var plugin = BuildWithMixedTools();
            var toolManager = plugin.UcoManager.ToolManager!;
            var systemToolManager = plugin.UcoManager.SystemToolManager!;

            toolManager.TotalToolsCount.ShouldBe(3); // standard-tool-a, standard-tool-b, standard-default
            systemToolManager.TotalToolsCount.ShouldBe(2); // system-tool-x, system-tool-y
        }

        // ── System tool execution ───────────────────────────────────────

        [Fact]
        public async Task RunSystemTool_ExistingTool_ShouldSucceed()
        {
            var plugin = BuildWithMixedTools();
            var systemToolManager = plugin.UcoManager.SystemToolManager!;

            var request = new RequestCallTool("system-tool-x", new Dictionary<string, JsonElement>());
            var response = await systemToolManager.RunSystemTool(request);

            response.ShouldNotBeNull();
            response.Status.ShouldBe(ResponseStatus.Success);
        }

        [Fact]
        public async Task RunSystemTool_NonExistentTool_ShouldReturnError()
        {
            var plugin = BuildWithMixedTools();
            var systemToolManager = plugin.UcoManager.SystemToolManager!;

            var request = new RequestCallTool("nonexistent-tool", new Dictionary<string, JsonElement>());
            var response = await systemToolManager.RunSystemTool(request);

            response.ShouldNotBeNull();
            response.Status.ShouldBe(ResponseStatus.Error);
            response.Message!.ShouldContain("not found");
        }

        [Fact]
        public async Task RunSystemTool_NullRequest_ShouldReturnError()
        {
            var plugin = BuildWithMixedTools();
            var systemToolManager = plugin.UcoManager.SystemToolManager!;

            var response = await systemToolManager.RunSystemTool(null!);

            response.ShouldNotBeNull();
            response.Status.ShouldBe(ResponseStatus.Error);
        }

        [Fact]
        public async Task RunSystemTool_EmptyName_ShouldReturnError()
        {
            var plugin = BuildWithMixedTools();
            var systemToolManager = plugin.UcoManager.SystemToolManager!;

            var request = new RequestCallTool("", new Dictionary<string, JsonElement>());
            var response = await systemToolManager.RunSystemTool(request);

            response.ShouldNotBeNull();
            response.Status.ShouldBe(ResponseStatus.Error);
            response.Message!.ShouldContain("empty");
        }

        // ── Standard tool listing excludes system tools ─────────────────

        [Fact]
        public async Task ListTools_ShouldOnlyReturnStandardTools()
        {
            var plugin = BuildWithMixedTools();
            var toolManager = plugin.UcoManager.ToolManager!;

            var response = await toolManager.RunListTool(new RequestListTool());

            response.ShouldNotBeNull();
            response.Status.ShouldBe(ResponseStatus.Success);

            var names = response.Value!.Select(t => t.Name).ToList();
            names.ShouldContain("standard-tool-a");
            names.ShouldContain("standard-tool-b");
            names.ShouldContain("standard-default");
            names.ShouldNotContain("system-tool-x");
            names.ShouldNotContain("system-tool-y");
        }

        [Fact]
        public async Task ListSystemTools_ShouldPreserveExplicitSafetyHints()
        {
            var plugin = BuildWithMixedTools();
            var response = await plugin.UcoManager.SystemToolManager!
                .RunListSystemTool(new RequestListTool());

            response.Status.ShouldBe(ResponseStatus.Success);
            var tool = response.Value!.Single(entry => entry.Name == "system-tool-x");
            tool.ReadOnlyHint.ShouldBe(true);
            tool.DestructiveHint.ShouldBe(false);
            tool.IdempotentHint.ShouldBe(true);
            tool.OpenWorldHint.ShouldBe(false);
        }

        [Fact]
        public async Task ListSystemTools_ShouldKeepUnsetSafetyHintsNull()
        {
            var plugin = BuildWithMixedTools();
            var response = await plugin.UcoManager.SystemToolManager!
                .RunListSystemTool(new RequestListTool());

            response.Status.ShouldBe(ResponseStatus.Success);
            var tool = response.Value!.Single(entry => entry.Name == "system-tool-y");
            tool.ReadOnlyHint.ShouldBeNull();
            tool.DestructiveHint.ShouldBeNull();
            tool.IdempotentHint.ShouldBeNull();
            tool.OpenWorldHint.ShouldBeNull();

            using var services = new ServiceCollection().BuildServiceProvider();
            var provider = new WebSocketConnectionProvider(
                NullLogger<ClientWebSocket>.Instance,
                new Reflector(),
                services);
            var options = provider.JsonSerializerOptions;
            var envelope = new WsResponse
            {
                Id = "system-list",
                Result = JsonSerializer.SerializeToElement(response, options),
            };
            using var wireJson = JsonDocument.Parse(WsEnvelope.SerializeResponse(envelope, options));
            var wireTool = wireJson.RootElement
                .GetProperty("result")
                .GetProperty("value")
                .EnumerateArray()
                .Single(entry => entry.GetProperty("name").GetString() == "system-tool-y");
            AssertUnknownOrOmitted(wireTool, "readOnlyHint");
            AssertUnknownOrOmitted(wireTool, "destructiveHint");
            AssertUnknownOrOmitted(wireTool, "idempotentHint");
            AssertUnknownOrOmitted(wireTool, "openWorldHint");
        }

        private static void AssertUnknownOrOmitted(JsonElement value, string propertyName)
        {
            if (value.TryGetProperty(propertyName, out var property))
                property.ValueKind.ShouldBe(JsonValueKind.Null);
        }

        // ── SystemToolManager available when no system tools registered ─

        [Fact]
        public void Build_NoSystemTools_SystemToolManagerShouldStillExist()
        {
            var reflector = new Reflector();
            var builder = new UcoBuilder(_version, _loggerProvider)
                .AddLogging(b => b.AddXunitTestOutput(_output))
                .WithTools(typeof(AnnotatedToolClass));
            var plugin = builder.Build(reflector);

            plugin.UcoManager.SystemToolManager.ShouldNotBeNull();
            plugin.UcoManager.SystemToolManager!.TotalToolsCount.ShouldBe(0);
        }

    }
}
