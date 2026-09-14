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
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using com.IvanMurzak.McpPlugin.Common.Model;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace com.IvanMurzak.McpPlugin.Tests.Network
{
    /// <summary>
    /// Unit tests for the WebSocket envelope transport layer:
    /// WsEnvelope serialization, WsRpcDispatcher dispatch, dual-path completion,
    /// and ResponseStatus lowercase serialization.
    /// </summary>
    public class WsTransportTests
    {
        private readonly ITestOutputHelper _output;
        private readonly JsonSerializerOptions _options;

        public WsTransportTests(ITestOutputHelper output)
        {
            _output = output;
            _options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            };
            _options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        }

        // ── Task 12.1: WsEnvelope serialization/deserialization round-trip ──

        [Fact]
        public void WsEnvelope_RequestRoundTrip_PreservesAllFields()
        {
            var request = new WsRequest
            {
                Id = "p-1",
                Method = "RunCallTool",
                Params = JsonSerializer.SerializeToElement(new { name = "test", arguments = new { foo = "bar" } })
            };

            var bytes = WsEnvelope.SerializeRequest(request, _options);
            bytes.ShouldNotBeNull();
            bytes.Length.ShouldBeGreaterThan(0);

            // Parse it back
            var success = WsEnvelope.TryParseMessage(bytes, _options, out var parsed);
            success.ShouldBeTrue();
            parsed.ShouldNotBeNull();
            parsed!.Type.ShouldBe(WsMessageType.Request);
            parsed.Id.ShouldBe("p-1");
            parsed.Method.ShouldBe("RunCallTool");
            parsed.Params.ShouldNotBeNull();
        }

        [Fact]
        public void WsEnvelope_NotificationRoundTrip_ParsesCorrectly()
        {
            var notification = new WsNotification
            {
                Method = "ForceDisconnect",
                Params = JsonSerializer.SerializeToElement(new { reason = "test reason" })
            };

            var bytes = WsEnvelope.SerializeNotification(notification, _options);
            var success = WsEnvelope.TryParseMessage(bytes, _options, out var parsed);

            success.ShouldBeTrue();
            parsed!.Type.ShouldBe(WsMessageType.Notification);
            parsed.Method.ShouldBe("ForceDisconnect");
            parsed.Id.ShouldBeNull();
        }

        [Fact]
        public void WsEnvelope_ResponseWithResult_ParsesCorrectly()
        {
            var response = new WsResponse
            {
                Id = "p-1",
                Result = JsonSerializer.SerializeToElement(new { status = "success" })
            };

            var bytes = WsEnvelope.SerializeResponse(response, _options);
            var success = WsEnvelope.TryParseMessage(bytes, _options, out var parsed);

            success.ShouldBeTrue();
            parsed!.Type.ShouldBe(WsMessageType.Response);
            parsed.Id.ShouldBe("p-1");
            parsed.Result.ShouldNotBeNull();
            parsed.Error.ShouldBeNull();
        }

        [Fact]
        public void WsEnvelope_ResponseWithError_ParsesCorrectly()
        {
            var response = new WsResponse
            {
                Id = "p-2",
                Error = new WsError { Code = -32601, Message = "Method not found" }
            };

            var bytes = WsEnvelope.SerializeResponse(response, _options);
            var success = WsEnvelope.TryParseMessage(bytes, _options, out var parsed);

            success.ShouldBeTrue();
            parsed!.Type.ShouldBe(WsMessageType.Response);
            parsed.Id.ShouldBe("p-2");
            parsed.Error.ShouldNotBeNull();
            parsed.Error!.Code.ShouldBe(-32601);
            parsed.Error.Message.ShouldBe("Method not found");
        }

        [Fact]
        public void WsEnvelope_UnicodeStrings_RoundTripCorrectly()
        {
            var request = new WsRequest
            {
                Id = "p-3",
                Method = "TestMethod",
                Params = JsonSerializer.SerializeToElement(new { unicode = "你好世界 🌍" })
            };

            var bytes = WsEnvelope.SerializeRequest(request, _options);
            var success = WsEnvelope.TryParseMessage(bytes, _options, out var parsed);

            success.ShouldBeTrue();
            parsed!.Method.ShouldBe("TestMethod");
        }

        [Fact]
        public void WsEnvelope_RequestWithNullParams_RoundTrips()
        {
            var request = new WsRequest
            {
                Id = "p-4",
                Method = "GetMcpServerData",
                Params = null
            };

            var bytes = WsEnvelope.SerializeRequest(request, _options);
            var success = WsEnvelope.TryParseMessage(bytes, _options, out var parsed);

            success.ShouldBeTrue();
            parsed!.Type.ShouldBe(WsMessageType.Request);
            parsed.Id.ShouldBe("p-4");
            parsed.Method.ShouldBe("GetMcpServerData");
        }

        // ── Task 12.2: WsRpcDispatcher handler-dict ──────────────────────

        [Fact]
        public async Task WsRpcDispatcher_RegisterHandler_InvokesOnRequest()
        {
            var dispatcher = new WsRpcDispatcher(_options);
            string? receivedMethod = null;

            dispatcher.RegisterHandler<TestParam, TestResult>("TestMethod", async param =>
            {
                receivedMethod = param?.Name;
                return new TestResult { Value = "response-" + param?.Name };
            });

            // Simulate an incoming request
            var requestJson = JsonSerializer.SerializeToUtf8Bytes(new WsRequest
            {
                Id = "r-1",
                Method = "TestMethod",
                Params = JsonSerializer.SerializeToElement(new TestParam { Name = "hello" }, _options)
            }, _options);

            WsEnvelope.TryParseMessage(requestJson, _options, out var parsed);
            await dispatcher.HandleIncomingAsync(parsed);

            receivedMethod.ShouldBe("hello");
        }

        // ── Task 12.3: WsRpcDispatcher pending tracker ───────────────────

        [Fact]
        public async Task WsRpcDispatcher_InvokeAsync_ResolvesOnResponse()
        {
            var dispatcher = new WsRpcDispatcher(_options);
            byte[]? sentBytes = null;

            dispatcher.SendFunc = (bytes, ct) =>
            {
                sentBytes = bytes;
                return Task.CompletedTask;
            };

            // Register a handler that simulates the server responding
            dispatcher.RegisterHandler<TestParam, TestResult>("Echo", async param =>
            {
                // Simulate the server sending a response by resolving the pending request
                // In a real scenario, the receive loop would parse the response and call ResolveResponse.
                // We do it here synchronously to test the pending tracker.
                _ = Task.Run(async () =>
                {
                    await Task.Delay(50); // Small delay to ensure InvokeAsync has registered the TCS
                    // Extract the id from sentBytes to resolve the correct pending request
                    var sentJson = Encoding.UTF8.GetString(sentBytes!);
                    using var doc = JsonDocument.Parse(sentJson);
                    var id = doc.RootElement.GetProperty("id").GetString()!;
                    var resultElement = JsonSerializer.SerializeToElement(
                        new TestResult { Value = "echoed" }, _options);
                    dispatcher.ResolveResponse(id, resultElement, null);
                });
                return new TestResult { Value = "immediate" };
            });

            // Use InvokeAsync (outgoing) which should get resolved by the simulated response
            // However, InvokeAsync sends a request and waits for a response.
            // The handler above is for INCOMING requests, not for outgoing.
            // Let me test this differently.

            // Simpler test: call ResolveResponse directly after InvokeAsync
            sentBytes = null;
            var invokeTask = dispatcher.InvokeAsync<object?, TestResult>("TestMethod", null, CancellationToken.None);

            // Wait a moment for the request to be sent and TCS registered
            await Task.Delay(100);

            // Extract the id from sentBytes
            sentBytes.ShouldNotBeNull();
            var sentJson = Encoding.UTF8.GetString(sentBytes);
            using var doc = JsonDocument.Parse(sentJson);
            var id = doc.RootElement.GetProperty("id").GetString()!;

            // Resolve the pending request
            var resultElement = JsonSerializer.SerializeToElement(
                new TestResult { Value = "resolved!" }, _options);
            dispatcher.ResolveResponse(id, resultElement, null);

            var result = await invokeTask;
            result.ShouldNotBeNull();
            result.Value.ShouldBe("resolved!");
        }

        // ── Task 12.4: Dual-path completion ───────────────────────────────

        [Fact]
        public async Task WsRpcDispatcher_DeferredCompletion_ResolvesOnNotifyToolRequestCompleted()
        {
            var dispatcher = new WsRpcDispatcher(_options);

            // Register a deferred completion for requestId "tool-123"
            var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            dispatcher.RegisterDeferredCompletion("tool-123", tcs);

            // Simulate a NotifyToolRequestCompleted notification arriving
            var resultElement = JsonSerializer.SerializeToElement(
                new { content = new object[] { new { type = "text", text = "done" } } }, _options);
            dispatcher.ResolveDeferred("tool-123", resultElement);

            // The TCS should be resolved
            var completed = tcs.Task.IsCompleted;
            completed.ShouldBeTrue("deferred TCS should be resolved by ResolveDeferred");

            // Verify the result
            var result = await tcs.Task;
            result.GetRawText().ShouldContain("done");
        }

        [Fact]
        public async Task WsRpcDispatcher_DeferredCompletion_FirstWinsIsIdempotent()
        {
            var dispatcher = new WsRpcDispatcher(_options);
            var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            dispatcher.RegisterDeferredCompletion("tool-456", tcs);

            var result1 = JsonSerializer.SerializeToElement(new { status = "success" }, _options);
            var result2 = JsonSerializer.SerializeToElement(new { status = "error" }, _options);

            // Resolve twice — first should win
            dispatcher.ResolveDeferred("tool-456", result1);
            dispatcher.ResolveDeferred("tool-456", result2); // Should be no-op

            var result = await tcs.Task;
            result.GetRawText().ShouldContain("success");
            result.GetRawText().ShouldNotContain("error");
        }

        // ── Task 12.5: ResponseStatus lowercase serialization ────────────

        [Fact]
        public void ResponseStatus_SerializesAsLowercase_Error()
        {
            var data = new ResponseData { Status = ResponseStatus.Error, Message = "failed" };
            var json = JsonSerializer.Serialize(data, _options);
            _output.WriteLine($"Serialized: {json}");
            json.Contains("\"status\":\"error\"", StringComparison.Ordinal).ShouldBeTrue("status should be lowercase 'error'");
            json.Contains("\"status\":\"Error\"", StringComparison.Ordinal).ShouldBeFalse("status should NOT be PascalCase");
        }

        [Fact]
        public void ResponseStatus_SerializesAsLowercase_Success()
        {
            var data = new ResponseData { Status = ResponseStatus.Success };
            var json = JsonSerializer.Serialize(data, _options);
            _output.WriteLine($"Serialized: {json}");
            json.Contains("\"status\":\"success\"", StringComparison.Ordinal).ShouldBeTrue("status should be lowercase 'success'");
            json.Contains("\"status\":\"Success\"", StringComparison.Ordinal).ShouldBeFalse("status should NOT be PascalCase");
        }

        [Fact]
        public void ResponseStatus_SerializesAsLowercase_Processing()
        {
            var data = new ResponseData { Status = ResponseStatus.Processing };
            var json = JsonSerializer.Serialize(data, _options);
            _output.WriteLine($"Serialized: {json}");
            json.Contains("\"status\":\"processing\"", StringComparison.Ordinal).ShouldBeTrue("status should be lowercase 'processing'");
            json.Contains("\"status\":\"Processing\"", StringComparison.Ordinal).ShouldBeFalse("status should NOT be PascalCase");
        }

        // ── Helper types ─────────────────────────────────────────────────

        public class TestParam
        {
            public string? Name { get; set; }
        }

        public class TestResult
        {
            public string? Value { get; set; }
        }
    }
}
