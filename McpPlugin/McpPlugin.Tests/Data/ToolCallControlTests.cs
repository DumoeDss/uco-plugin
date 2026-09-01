/*
┌────────────────────────────────────────────────────────────────────────┐
│  Author: Ivan Murzak (https://github.com/IvanMurzak)                   │
│  Repository: GitHub (https://github.com/IvanMurzak/MCP-Plugin-dotnet)  │
│  Copyright (c) 2025 Ivan Murzak                                        │
│  Licensed under the Apache License, Version 2.0.                       │
│  See the LICENSE file in the project root for more information.        │
└────────────────────────────────────────────────────────────────────────┘
*/
#nullable enable

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using com.IvanMurzak.McpPlugin.Common.Model;
using Shouldly;
using Xunit;

namespace com.IvanMurzak.McpPlugin.Tests.Data
{
    public class ToolCallControlTests
    {
        private static readonly JsonSerializerOptions WireOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
        };

        [Fact]
        public void RequestCallTool_DeserializesLegacyAndControlledShapes()
        {
            var legacy = JsonSerializer.Deserialize<RequestCallTool>(
                "{\"name\":\"ping\",\"arguments\":{},\"requestID\":\"legacy-1\"}",
                WireOptions);
            var controlled = JsonSerializer.Deserialize<RequestCallTool>(
                "{\"name\":\"ping\",\"arguments\":{},\"requestID\":\"request-1\",\"control\":{\"callId\":\"call-1\",\"correlationId\":\"trace-1\",\"futureFlag\":true}}",
                WireOptions);

            legacy.ShouldNotBeNull();
            legacy!.Control.ShouldBeNull();
            controlled.ShouldNotBeNull();
            controlled!.Control.ShouldNotBeNull();
            controlled.Control!.Version.ShouldBe(ToolCallControl.CurrentVersion);
            controlled.Control.CallId.ShouldBe("call-1");
            controlled.Control.UnknownMembers["futureFlag"].GetBoolean().ShouldBeTrue();
        }

        [Fact]
        public void Normalize_LegacyCall_UsesRequestIdForLogicalDefaults()
        {
            var request = new RequestCallTool("legacy-1", "ping", EmptyArguments());

            var normalized = ToolCallContextNormalizer.Normalize(
                request,
                cancellationToken: CancellationToken.None,
                idFactory: () => "unused");

            normalized.Request.RequestID.ShouldBe("legacy-1");
            normalized.Context.RequestID.ShouldBe("legacy-1");
            normalized.Context.CallId.ShouldBe("legacy-1");
            normalized.Context.CorrelationId.ShouldBe("legacy-1");
            normalized.Context.Legacy.ShouldBeTrue();
            normalized.Context.ParentCallId.ShouldBeNull();
        }

        [Fact]
        public void Normalize_ControlledCall_PreservesUnknownAndSeparatesRequestId()
        {
            var request = new RequestCallTool(
                "request-1",
                "ping",
                EmptyArguments(),
                new ToolCallControl
                {
                    CallId = "call-1",
                    CorrelationId = "trace-1",
                    DeadlineUnixMs = 2_000,
                    CancellationId = "cancel-1",
                    IdempotencyKey = "opaque-1",
                    UnknownMembers = new Dictionary<string, JsonElement>
                    {
                        ["futureFlag"] = JsonSerializer.SerializeToElement(true),
                    },
                });

            var normalized = ToolCallContextNormalizer.Normalize(request);

            normalized.Request.RequestID.ShouldBe("request-1");
            normalized.Context.CallId.ShouldBe("call-1");
            normalized.Context.CorrelationId.ShouldBe("trace-1");
            normalized.Context.DeadlineUnixMs.ShouldBe(2_000);
            normalized.Context.CancellationId.ShouldBe("cancel-1");
            normalized.Context.IdempotencyKey.ShouldBe("opaque-1");
            normalized.Context.UnknownMembers["futureFlag"].GetBoolean().ShouldBeTrue();
        }

        [Fact]
        public void Normalize_UnsupportedVersion_FailsBeforeExecution()
        {
            var request = new RequestCallTool(
                "request-1",
                "ping",
                EmptyArguments(),
                new ToolCallControl { Version = 2, CallId = "call-1" });

            var exception = Should.Throw<ToolCallControlException>(
                () => ToolCallContextNormalizer.Normalize(request));

            exception.Code.ShouldBe(ToolCallErrorCodes.UnsupportedControlVersion);
            exception.Details!.AsObject()["supportedVersion"]!.GetValue<int>().ShouldBe(1);
        }

        [Fact]
        public void DeriveChild_BoundsDeadlineAndDoesNotInheritIdempotency()
        {
            var parent = ToolCallContextNormalizer.Normalize(
                new RequestCallTool(
                    "request-parent",
                    "batch-execute",
                    EmptyArguments(),
                    new ToolCallControl
                    {
                        CallId = "call-parent",
                        CorrelationId = "trace-parent",
                        DeadlineUnixMs = 2_000,
                        IdempotencyKey = "parent-key",
                    })).Context;

            var child = ToolCallContextNormalizer.DeriveChild(
                parent,
                new ToolCallControl { CallId = "call-child", DeadlineUnixMs = 3_000 },
                idFactory: () => "unused");

            child.CallId.ShouldBe("call-child");
            child.CorrelationId.ShouldBe("trace-parent");
            child.ParentCallId.ShouldBe("call-parent");
            child.DeadlineUnixMs.ShouldBe(2_000);
            child.IdempotencyKey.ShouldBeNull();
            child.RequestID.ShouldBe("call-child");
        }

        [Fact]
        public void ResponseData_SerializesOptionalStructuredErrorAdditively()
        {
            var response = ResponseData<ResponseCallTool>.Error("request-1", "controlled failure");
            response.StructuredError = new ToolCallError(
                ToolCallErrorCodes.InvalidControl,
                "Tool call control metadata is invalid.",
                callId: "call-1",
                correlationId: "trace-1",
                details: new JsonObject { ["field"] = "deadlineUnixMs" });

            var json = JsonSerializer.Serialize(response, WireOptions);
            json.ShouldContain("\"error\"");
            json.ShouldContain("\"invalid_control\"");
            json.ShouldContain("\"callId\":\"call-1\"");
        }

        private static IReadOnlyDictionary<string, JsonElement> EmptyArguments()
            => new Dictionary<string, JsonElement>();
    }
}
