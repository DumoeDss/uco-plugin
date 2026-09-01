/*
┌────────────────────────────────────────────────────────────────────────┐
│  Author: Ivan Murzak (https://github.com/IvanMurzak)                   │
│  Repository: GitHub (https://github.com/IvanMurzak/MCP-Plugin-dotnet)  │
│  Copyright (c) 2025 Ivan Murzak                                        │
│  Licensed under the Apache License, Version 2.0.                       │
│  See the LICENSE file in the project root for more information.        │
└────────────────────────────────────────────────────────────────────────┘
*/
using System.Net.WebSockets;
using System.Threading.Tasks;
using System.Text.Json;
using com.IvanMurzak.McpPlugin.Common.Model;
using com.IvanMurzak.McpPlugin.Tests.Data.Annotations;
using com.IvanMurzak.McpPlugin.Tests.Infrastructure;
using com.IvanMurzak.ReflectorNet;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;
using Xunit.Abstractions;
using Version = com.IvanMurzak.McpPlugin.Common.Version;

namespace com.IvanMurzak.McpPlugin.Tests.Mcp
{
    [Collection("McpPlugin")]
    public class McpBuilderTests_ToolAnnotations
    {
        private readonly ITestOutputHelper _output;
        private readonly XunitTestOutputLoggerProvider _loggerProvider;
        private readonly Version version = new Version();

        public McpBuilderTests_ToolAnnotations(ITestOutputHelper output)
        {
            _output = output;
            _loggerProvider = new XunitTestOutputLoggerProvider(output);
        }

        private async Task<ResponseListTool> GetTool(string toolName)
        {
            var reflector = new Reflector();
            var mcpPluginBuilder = new McpPluginBuilder(version, _loggerProvider)
                .AddLogging(b => b.AddXunitTestOutput(_output))
                .WithTools(typeof(AnnotatedToolClass));

            var mcpPlugin = mcpPluginBuilder.Build(reflector);
            var request = new RequestListTool();
            var response = await mcpPlugin.McpManager.ToolManager!.RunListTool(request);

            response.ShouldNotBeNull();
            response.Status.ShouldBe(ResponseStatus.Success);
            response.Value.ShouldNotBeNull();

            var tool = System.Linq.Enumerable.FirstOrDefault(response.Value!, t => t.Name == toolName);
            tool.ShouldNotBeNull($"Tool '{toolName}' should be registered");
            return tool!;
        }

        [Fact]
        public async Task ToolAnnotations_NoHints_ShouldHaveNullHints()
        {
            var tool = await GetTool("tool-no-hints");

            tool.ReadOnlyHint.ShouldBeNull();
            tool.DestructiveHint.ShouldBeNull();
            tool.IdempotentHint.ShouldBeNull();
            tool.OpenWorldHint.ShouldBeNull();
        }

        [Fact]
        public async Task ToolAnnotations_ReadOnlyHint_ShouldBeTrue()
        {
            var tool = await GetTool("tool-readonly");

            tool.ReadOnlyHint.ShouldBe(true);
            tool.DestructiveHint.ShouldBeNull();
            tool.IdempotentHint.ShouldBeNull();
            tool.OpenWorldHint.ShouldBeNull();
        }

        [Fact]
        public async Task ToolAnnotations_DestructiveHintFalse_ShouldBeFalse()
        {
            var tool = await GetTool("tool-destructive-false");

            tool.ReadOnlyHint.ShouldBeNull();
            tool.DestructiveHint.ShouldBe(false);
            tool.IdempotentHint.ShouldBeNull();
            tool.OpenWorldHint.ShouldBeNull();
        }

        [Fact]
        public async Task ToolAnnotations_IdempotentHint_ShouldBeTrue()
        {
            var tool = await GetTool("tool-idempotent");

            tool.ReadOnlyHint.ShouldBeNull();
            tool.DestructiveHint.ShouldBeNull();
            tool.IdempotentHint.ShouldBe(true);
            tool.OpenWorldHint.ShouldBeNull();
        }

        [Fact]
        public async Task ToolAnnotations_OpenWorldHint_ShouldBeTrue()
        {
            var tool = await GetTool("tool-open-world");

            tool.ReadOnlyHint.ShouldBeNull();
            tool.DestructiveHint.ShouldBeNull();
            tool.IdempotentHint.ShouldBeNull();
            tool.OpenWorldHint.ShouldBe(true);
        }

        [Fact]
        public async Task ToolAnnotations_AllHints_ShouldBeCorrect()
        {
            var tool = await GetTool("tool-all-hints");

            tool.ReadOnlyHint.ShouldBe(true);
            tool.DestructiveHint.ShouldBe(false);
            tool.IdempotentHint.ShouldBe(true);
            tool.OpenWorldHint.ShouldBe(false);
        }

        [Fact]
        public async Task ToolAnnotations_ProductionWebSocketEnvelope_ShouldKeepFalseAndUnknownHints()
        {
            var annotated = await GetTool("tool-all-hints");
            var legacy = await GetTool("tool-no-hints");
            using var services = new ServiceCollection().BuildServiceProvider();
            var provider = new WebSocketConnectionProvider(
                NullLogger<ClientWebSocket>.Instance,
                new Reflector(),
                services);
            var options = provider.JsonSerializerOptions;
            var response = ResponseData<ResponseListTool[]>.Success("wire-list");
            response.Value = new[] { annotated, legacy };
            var envelope = new WsResponse
            {
                Id = "wire-list",
                Result = JsonSerializer.SerializeToElement(response, options),
            };

            using var wireJson = JsonDocument.Parse(WsEnvelope.SerializeResponse(envelope, options));
            var tools = wireJson.RootElement.GetProperty("result").GetProperty("value");
            var annotatedRoot = tools[0];
            annotatedRoot.GetProperty("readOnlyHint").GetBoolean().ShouldBeTrue();
            annotatedRoot.GetProperty("destructiveHint").GetBoolean().ShouldBeFalse();
            annotatedRoot.GetProperty("idempotentHint").GetBoolean().ShouldBeTrue();
            annotatedRoot.GetProperty("openWorldHint").GetBoolean().ShouldBeFalse();

            var legacyRoot = tools[1];
            AssertUnknownOrOmitted(legacyRoot, "readOnlyHint");
            AssertUnknownOrOmitted(legacyRoot, "destructiveHint");
            AssertUnknownOrOmitted(legacyRoot, "idempotentHint");
            AssertUnknownOrOmitted(legacyRoot, "openWorldHint");
        }

        private static void AssertUnknownOrOmitted(JsonElement value, string propertyName)
        {
            if (value.TryGetProperty(propertyName, out var property))
                property.ValueKind.ShouldBe(JsonValueKind.Null);
        }
    }
}
