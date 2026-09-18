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
using System.Text.Json;
using System.Threading.Tasks;
using com.AtelierAI.Uco.Framework.Common.Model;
using com.AtelierAI.Uco.Framework.Tests.Infrastructure;
using com.AtelierAI.Uco.Framework.Utils;
using com.IvanMurzak.ReflectorNet;
using Shouldly;
using Xunit;
using Xunit.Abstractions;
using Version = com.AtelierAI.Uco.Framework.Common.Version;

namespace com.AtelierAI.Uco.Framework.Tests.Managers
{
    public enum PromptTestEnum
    {
        OptionA,
        OptionB
    }

    public class PromptMethod_EnumDefaultValue
    {
        [UcoPrompt(Name = "test_prompt")]
        public string GetPrompt(PromptTestEnum? options = PromptTestEnum.OptionB)
        {
            return options?.ToString() ?? "null";
        }
    }

    [Collection("UcoPlugin")]
    public class UcoBuilderTests_PromptEnumDefaultValue
    {
        private readonly ITestOutputHelper _output;
        private readonly XunitTestOutputLoggerProvider _loggerProvider;
        private readonly Version _version = new Version();

        public UcoBuilderTests_PromptEnumDefaultValue(ITestOutputHelper output)
        {
            _output = output;
            _loggerProvider = new XunitTestOutputLoggerProvider(output);
        }

        private IUcoPlugin BuildUcoPluginWithPrompt(Type classType, string methodName)
        {
            var method = classType.GetMethod(methodName)!;
            var promptName = "test_prompt";

            var reflector = new Reflector();
            var ucoPluginBuilder = new UcoBuilder(_version, _loggerProvider)
                .AddLogging(b => b.AddXunitTestOutput(_output));

            ucoPluginBuilder.WithPrompt(
                name: promptName,
                classType: classType,
                methodInfo: method);

            return ucoPluginBuilder.Build(reflector)!;
        }

        [Fact]
        public async Task CallPrompt_WithEnumDefaultValue_ShouldSucceed()
        {
            // Arrange
            var ucoPlugin = BuildUcoPluginWithPrompt(typeof(PromptMethod_EnumDefaultValue), nameof(PromptMethod_EnumDefaultValue.GetPrompt));
            var promptName = "test_prompt";

            // Act - calling without arguments, expecting default value PromptTestEnum.OptionB
            var request = new RequestGetPrompt(promptName, new Dictionary<string, JsonElement>());
            var response = await ucoPlugin.UcoManager.PromptManager!.RunGetPrompt(request);

            // Assert
            response.ShouldNotBeNull();
            if (response.Status != ResponseStatus.Success)
            {
                _output.WriteLine($"Error: {response.Message}");
            }
            response.Status.ShouldBe(ResponseStatus.Success);
            response.Value.ShouldNotBeNull();
            response.Value!.Messages.ShouldNotBeNull();
            response.Value!.Messages.Count.ShouldBe(1);
            response.Value!.Messages![0].Content.Text.ShouldBe("OptionB");
        }
    }
}
