#nullable enable

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using com.AtelierAI.Uco.Framework.Common.Model;
using Shouldly;
using Xunit;

namespace com.AtelierAI.Uco.Framework.Tests.Data
{
    public sealed class AuthoringControlContractTests
    {
        private static readonly JsonSerializerOptions WireOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        [Fact]
        public void Normalize_ControlledCall_PreservesAuthoringFieldsAndUnknownMembers()
        {
            using var document = JsonDocument.Parse(
                "{\"name\":\"authoring-tool\",\"arguments\":{\"path\":\"Assets/Thing.prefab\"},\"requestID\":\"request-1\",\"control\":{\"version\":1,\"callId\":\"call-1\",\"correlationId\":\"trace-1\",\"confirm\":true,\"dryRun\":\"plan\",\"confirmation\":{\"planId\":\"plan-1\",\"planHash\":\"sha256-1\",\"expiresAtUnixMs\":2000,\"futureToken\":7},\"futureFlag\":true}}");

            var normalized = ToolCallContextNormalizer.Normalize(document.RootElement);

            normalized.Context.Confirm.ShouldBe(true);
            normalized.Context.DryRun.ShouldBe("plan");
            normalized.Context.Confirmation!.PlanId.ShouldBe("plan-1");
            normalized.Context.Confirmation.PlanHash.ShouldBe("sha256-1");
            normalized.Context.Confirmation.ExpiresAtUnixMs.ShouldBe(2_000);
            normalized.Context.Confirmation.UnknownMembers["futureToken"].GetInt32().ShouldBe(7);
            normalized.Context.UnknownMembers["futureFlag"].GetBoolean().ShouldBeTrue();

            var wire = JsonSerializer.Serialize(normalized.Request, WireOptions);
            wire.ShouldContain("\"confirm\":true");
            wire.ShouldContain("\"dryRun\":\"plan\"");
            wire.ShouldContain("\"planId\":\"plan-1\"");
            wire.ShouldContain("\"futureToken\":7");
            wire.ShouldContain("\"futureFlag\":true");
        }

        [Theory]
        [InlineData("confirm", "null")]
        [InlineData("dryRun", "true")]
        [InlineData("dryRun", "\"preview\"")]
        [InlineData("confirmation", "{}")]
        public void Normalize_MalformedAuthoringFields_FailsBeforeExecution(string member, string value)
        {
            using var document = JsonDocument.Parse(
                "{\"name\":\"authoring-tool\",\"arguments\":{},\"requestID\":\"request-1\",\"control\":{\"callId\":\"call-1\",\"" + member + "\":" + value + "}}");

            var exception = Should.Throw<ToolCallControlException>(
                () => ToolCallContextNormalizer.Normalize(document.RootElement));
            exception.Code.ShouldBe(ToolCallErrorCodes.InvalidControl);
        }

        [Fact]
        public void LegacyCallRetainsBareShapeAndContextCancellationIsNotSerialized()
        {
            var request = new RequestCallTool(
                "legacy-request",
                "read-tool",
                new Dictionary<string, JsonElement>());
            var cancellation = new CancellationTokenSource();
            var normalized = ToolCallContextNormalizer.Normalize(request, cancellation.Token);

            normalized.Context.Legacy.ShouldBeTrue();
            normalized.Context.DryRun.ShouldBe("none");
            normalized.Context.CancellationToken.CanBeCanceled.ShouldBeTrue();
            var json = JsonSerializer.Serialize(normalized.Context, WireOptions);
            json.ShouldNotContain("CancellationToken");
            json.ShouldNotContain("cancellationToken");
        }
    }
}
