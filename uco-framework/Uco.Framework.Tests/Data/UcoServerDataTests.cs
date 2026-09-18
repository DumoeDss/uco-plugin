/*
┌────────────────────────────────────────────────────────────────────────┐
│  Author: Ivan Murzak (https://github.com/IvanMurzak)                   │
│  Repository: GitHub (https://github.com/IvanMurzak/MCP-Plugin-dotnet)  │
│  Copyright (c) 2025 Ivan Murzak                                        │
│  Licensed under the Apache License, Version 2.0.                       │
│  See the LICENSE file in the project root for more information.        │
└────────────────────────────────────────────────────────────────────────┘
*/
using System.Text.Json;
using com.AtelierAI.Uco.Framework.Common;
using com.AtelierAI.Uco.Framework.Common.Model;
using Shouldly;
using Xunit;

namespace com.AtelierAI.Uco.Framework.Tests.Data
{
    public class UcoServerDataTests
    {
        [Fact]
        public void UcoServerData_DefaultConstructor_InitializesWithDefaultValues()
        {
            // Act
            var serverData = new UcoServerData();

            // Assert
            serverData.ServerVersion.ShouldBeNull();
            serverData.ServerApiVersion.ShouldBeNull();
            serverData.IsAiAgentConnected.ShouldBeFalse();
        }

        [Fact]
        public void UcoServerData_CanSetServerVersion()
        {
            // Arrange
            var serverData = new UcoServerData();

            // Act
            serverData.ServerVersion = "1.2.3";

            // Assert
            serverData.ServerVersion.ShouldBe("1.2.3");
        }

        [Fact]
        public void UcoServerData_CanSetServerApiVersion()
        {
            // Arrange
            var serverData = new UcoServerData();

            // Act
            serverData.ServerApiVersion = "2.0.0";

            // Assert
            serverData.ServerApiVersion.ShouldBe("2.0.0");
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void UcoServerData_CanSetIsAiAgentConnected(bool isConnected)
        {
            // Arrange
            var serverData = new UcoServerData();

            // Act
            serverData.IsAiAgentConnected = isConnected;

            // Assert
            serverData.IsAiAgentConnected.ShouldBe(isConnected);
        }

        [Fact]
        public void UcoServerData_ObjectInitializer_ShouldSetAllProperties()
        {
            // Act
            var serverData = new UcoServerData
            {
                ServerVersion = "1.0.0",
                ServerApiVersion = "1.0.0",
                IsAiAgentConnected = true
            };

            // Assert
            serverData.ServerVersion.ShouldBe("1.0.0");
            serverData.ServerApiVersion.ShouldBe("1.0.0");
            serverData.IsAiAgentConnected.ShouldBeTrue();
        }

        [Fact]
        public void UcoServerData_Serialize_ShouldUseJsonPropertyNames()
        {
            // Arrange
            var serverData = new UcoServerData
            {
                ServerVersion = "1.0.0",
                ServerApiVersion = "2.0.0",
                IsAiAgentConnected = true
            };

            // Act
            var json = JsonSerializer.Serialize(serverData);

            // Assert
            json.ShouldContain("\"serverVersion\":");
            json.ShouldContain("\"serverApiVersion\":");
            json.ShouldContain("\"isAiAgentConnected\":");
        }

        [Fact]
        public void UcoServerData_RoundTrip_ShouldPreserveAllValues()
        {
            // Arrange
            var original = new UcoServerData
            {
                ServerVersion = "1.2.3",
                ServerApiVersion = "1.0.0",
                IsAiAgentConnected = true
            };

            // Act
            var json = JsonSerializer.Serialize(original);
            var deserialized = JsonSerializer.Deserialize<UcoServerData>(json);

            // Assert
            deserialized.ShouldNotBeNull();
            deserialized!.ServerVersion.ShouldBe(original.ServerVersion);
            deserialized.ServerApiVersion.ShouldBe(original.ServerApiVersion);
            deserialized.IsAiAgentConnected.ShouldBe(original.IsAiAgentConnected);
        }
    }
}
