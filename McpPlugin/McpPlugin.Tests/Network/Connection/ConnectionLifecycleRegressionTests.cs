using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using com.IvanMurzak.McpPlugin.Common.Model;
using com.IvanMurzak.McpPlugin.Skills;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using R3;
using Shouldly;
using Xunit;

namespace com.IvanMurzak.McpPlugin.Tests.Network.Connection
{
    public sealed class ConnectionLifecycleRegressionTests
    {
        static readonly Common.Version TestVersion = new()
        {
            Api = "1.0.0",
            Plugin = "1.0.0",
            Environment = "test"
        };

        [Fact]
        public async Task HandshakeStall_TwentyRealManagerCyclesExhaustThenExplicitReplacementConnects()
        {
            var behaviors = Enumerable.Range(0, 20)
                .SelectMany(_ => new[]
                {
                    HandshakeBehavior.Stall,
                    HandshakeBehavior.Stall,
                    HandshakeBehavior.Stall,
                    HandshakeBehavior.Compatible
                })
                .ToArray();
            await using var server = new LoopbackWebSocketServer(behaviors);
            await using var manager = CreateManager(server);
            using var connector = new TestConnector(manager);
            var states = new ConcurrentQueue<ConnectionState>();
            using var stateSubscription = manager.ConnectionState.Subscribe(states.Enqueue);
            var terminalAttemptIds = new HashSet<long>();

            for (var cycle = 0; cycle < 20; cycle++)
            {
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
                    (await manager.Connect(timeout.Token)).ShouldBeFalse();

                manager.ConnectionGeneration.ShouldBe(cycle * 4 + 3);
                manager.LastAttempt.Stage.ShouldBe("handshake-failed");
                manager.LastAttempt.Terminal.ShouldBeTrue();
                manager.ActiveAttemptId.ShouldBe(0);
                manager.ConnectionState.CurrentValue.ShouldBe(ConnectionState.Disconnected);
                terminalAttemptIds.Add(manager.LastAttempt.AttemptId).ShouldBeTrue();

                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
                    (await manager.Connect(timeout.Token)).ShouldBeTrue();

                manager.ConnectionGeneration.ShouldBe((cycle + 1) * 4);
                manager.ConnectionState.CurrentValue.ShouldBe(ConnectionState.Connected);
                manager.LastAttempt.Stage.ShouldBe("connected");
                manager.LastAttempt.Terminal.ShouldBeTrue();
                terminalAttemptIds.Add(manager.LastAttempt.AttemptId).ShouldBeTrue();

                if (cycle < 19)
                    await manager.Disconnect();
            }

            manager.ConnectionGeneration.ShouldBe(80);
            manager.ConnectionState.CurrentValue.ShouldBe(ConnectionState.Connected);
            states.ShouldContain(ConnectionState.Reconnecting);
            states.Count(state => state == ConnectionState.Connected).ShouldBe(20);
            connector.HandlerRegistrationCount.ShouldBe(80);
            connector.ConnectedCallbackCount.ShouldBe(20);
            server.HandshakeRequestCount.ShouldBe(80);
            terminalAttemptIds.Count.ShouldBe(40);
        }

        [Fact]
        public async Task DirectConcurrentConnect_CancelsHandshakeStallBeforeReplacementOwnsNextGeneration()
        {
            await using var server = new LoopbackWebSocketServer(
                HandshakeBehavior.Stall,
                HandshakeBehavior.Compatible);
            await using var manager = CreateManager(
                server,
                TimeSpan.FromSeconds(5));
            using var connector = new TestConnector(manager);
            var states = new ConcurrentQueue<ConnectionState>();
            using var stateSubscription = manager.ConnectionState.Subscribe(states.Enqueue);

            using var staleTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var stale = manager.Connect(staleTimeout.Token);
            await AwaitUntil(
                () => server.HandshakeRequestCount == 1,
                TimeSpan.FromSeconds(2));
            var staleAttemptId = manager.ActiveAttemptId;
            staleAttemptId.ShouldBeGreaterThan(0);
            manager.ConnectionGeneration.ShouldBe(1);

            using var replacementTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var replacement = manager.Connect(replacementTimeout.Token);

            await AwaitWithin(Task.WhenAll(stale, replacement), TimeSpan.FromSeconds(3));
            (await stale).ShouldBeFalse();
            (await replacement).ShouldBeTrue();

            staleTimeout.IsCancellationRequested.ShouldBeFalse(
                "replacement must cancel the stale manager-owned token before the caller deadline");
            manager.ConnectionGeneration.ShouldBe(2);
            manager.ConnectionState.CurrentValue.ShouldBe(ConnectionState.Connected);
            manager.LastAttempt.AttemptId.ShouldBeGreaterThan(staleAttemptId);
            manager.LastAttempt.Stage.ShouldBe("connected");
            manager.LastAttempt.Terminal.ShouldBeTrue();
            manager.ActiveAttemptId.ShouldBe(0);
            states.ShouldContain(ConnectionState.Reconnecting);
            states.Count(state => state == ConnectionState.Connected).ShouldBe(1);
            connector.HandlerRegistrationCount.ShouldBe(2);
            connector.ConnectedCallbackCount.ShouldBe(1);
            server.HandshakeRequestCount.ShouldBe(2);
        }

        [Fact]
        public async Task TransportClose_RealManagerTransitionsThroughReconnectingAndRegistersNextGeneration()
        {
            await using var server = new LoopbackWebSocketServer(
                HandshakeBehavior.CompatibleThenClose,
                HandshakeBehavior.Compatible);
            await using var manager = CreateManager(server);
            using var connector = new TestConnector(manager);
            var states = new ConcurrentQueue<ConnectionState>();
            using var stateSubscription = manager.ConnectionState.Subscribe(states.Enqueue);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            (await manager.Connect(timeout.Token)).ShouldBeTrue();
            await AwaitUntil(
                () => manager.ConnectionGeneration == 2 &&
                    manager.ConnectionState.CurrentValue == ConnectionState.Connected,
                TimeSpan.FromSeconds(3));

            manager.ConnectionGeneration.ShouldBe(2);
            manager.ConnectionState.CurrentValue.ShouldBe(ConnectionState.Connected);
            states.ShouldContain(ConnectionState.Reconnecting);
            states.Count(state => state == ConnectionState.Connected).ShouldBe(2);
            connector.HandlerRegistrationCount.ShouldBe(2);
            connector.ConnectedCallbackCount.ShouldBe(2);
            server.HandshakeRequestCount.ShouldBe(2);
        }

        [Fact]
        public async Task DelayedCapabilityRegistration_HoldsConnectingUntilAllRegistrationCompletes()
        {
            await using var server = new LoopbackWebSocketServer(HandshakeBehavior.Compatible);
            await using var manager = CreateManager(server);
            using var connector = new TestConnector(manager);
            var registrationEntered = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseRegistration = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var ordering = new ConcurrentQueue<string>();
            using var stateSubscription = manager.ConnectionState.Subscribe(state =>
            {
                if (state == ConnectionState.Connected)
                    ordering.Enqueue("connected");
            });
            connector.HandlerRegistered = () => ordering.Enqueue("handlers");
            connector.SetCapabilityRegistrationHandler(async cancellationToken =>
            {
                ordering.Enqueue("capabilities-start");
                registrationEntered.TrySetResult(true);
                await releaseRegistration.Task.WaitAsync(cancellationToken);
                ordering.Enqueue("capabilities-done");
            });

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var connect = manager.Connect(timeout.Token);
            await AwaitWithin(registrationEntered.Task, TimeSpan.FromSeconds(2));

            connect.IsCompleted.ShouldBeFalse();
            manager.ConnectionState.CurrentValue.ShouldBe(ConnectionState.Connecting);
            ordering.ShouldNotContain("connected");

            releaseRegistration.TrySetResult(true);
            (await connect).ShouldBeTrue();

            ordering.ToArray().ShouldBe(new[]
            {
                "handlers",
                "capabilities-start",
                "capabilities-done",
                "connected"
            });
            connector.HandlerRegistrationCount.ShouldBe(1);
            connector.ConnectedCallbackCount.ShouldBe(1);
        }

        [Fact]
        public async Task FailingCapabilityRegistration_NeverPublishesConnectedAndExhaustsOwnedAttempts()
        {
            await using var server = new LoopbackWebSocketServer(
                HandshakeBehavior.Compatible,
                HandshakeBehavior.Compatible,
                HandshakeBehavior.Compatible);
            await using var manager = CreateManager(server);
            using var connector = new TestConnector(manager);
            var states = new ConcurrentQueue<ConnectionState>();
            using var stateSubscription = manager.ConnectionState.Subscribe(states.Enqueue);
            connector.SetCapabilityRegistrationHandler(_ =>
                Task.FromException(new InvalidOperationException("capability registration failed")));

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            (await manager.Connect(timeout.Token)).ShouldBeFalse();

            manager.ConnectionGeneration.ShouldBe(3);
            manager.ConnectionState.CurrentValue.ShouldBe(ConnectionState.Disconnected);
            manager.LastAttempt.Stage.ShouldBe("registration-failed");
            manager.LastAttempt.RootCause.ShouldNotBeNull();
            manager.LastAttempt.RootCause!.ShouldContain("capability registration failed");
            manager.ActiveAttemptId.ShouldBe(0);
            states.ShouldContain(ConnectionState.Reconnecting);
            states.ShouldNotContain(ConnectionState.Connected);
            connector.HandlerRegistrationCount.ShouldBe(3);
            connector.ConnectedCallbackCount.ShouldBe(3);
            server.HandshakeRequestCount.ShouldBe(3);
        }

        [Fact]
        public async Task McpPlugin_RegistersPromptsAndResourcesBeforeToolsEligibilitySignal()
        {
            Func<CancellationToken, Task>? registration = null;
            var order = new List<string>();
            var hub = Hub(registrationHandler => registration = registrationHandler);
            hub.Setup(x => x.NotifyAboutUpdatedPrompts(
                    It.IsAny<RequestPromptsUpdated>(), It.IsAny<CancellationToken>()))
                .Callback(() => order.Add("prompts"))
                .ReturnsAsync(Success());
            hub.Setup(x => x.NotifyAboutUpdatedResources(
                    It.IsAny<RequestResourcesUpdated>(), It.IsAny<CancellationToken>()))
                .Callback(() => order.Add("resources"))
                .ReturnsAsync(Success());
            hub.Setup(x => x.NotifyAboutUpdatedTools(
                    It.IsAny<RequestToolsUpdated>(), It.IsAny<CancellationToken>()))
                .Callback(() => order.Add("tools"))
                .ReturnsAsync(Success());

            using var plugin = Plugin(hub.Object);
            registration.ShouldNotBeNull();
            await registration!(CancellationToken.None);

            order.ShouldBe(new[] { "prompts", "resources", "tools" });
        }

        [Fact]
        public async Task McpPlugin_CapabilityFailureStopsBeforeToolsEligibilitySignal()
        {
            Func<CancellationToken, Task>? registration = null;
            var hub = Hub(registrationHandler => registration = registrationHandler);
            hub.Setup(x => x.NotifyAboutUpdatedPrompts(
                    It.IsAny<RequestPromptsUpdated>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Success());
            hub.Setup(x => x.NotifyAboutUpdatedResources(
                    It.IsAny<RequestResourcesUpdated>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ResponseData
                {
                    Status = ResponseStatus.Error,
                    Message = "resource registration rejected"
                });

            using var plugin = Plugin(hub.Object);
            registration.ShouldNotBeNull();
            var exception = await Should.ThrowAsync<InvalidOperationException>(
                () => registration!(CancellationToken.None));

            exception.Message.ShouldContain("resource registration rejected");
            hub.Verify(x => x.NotifyAboutUpdatedTools(
                It.IsAny<RequestToolsUpdated>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        static FastConnectionManager CreateManager(
            LoopbackWebSocketServer server,
            TimeSpan? initializationTimeout = null)
        {
            var provider = new Mock<IWebSocketConnectionProvider>();
            provider.SetupGet(x => x.JsonSerializerOptions).Returns(server.JsonOptions);
            provider.Setup(x => x.CreateConnectionAsync(It.IsAny<string>()))
                .ReturnsAsync(() =>
                {
                    var socket = new ClientWebSocket();
                    socket.Options.Proxy = null;
                    return (socket, server.WebSocketUri);
                });
            return new FastConnectionManager(
                provider.Object,
                initializationTimeout ?? TimeSpan.FromMilliseconds(100));
        }

        static Mock<IMcpManagerHub> Hub(Action<Func<CancellationToken, Task>> capture)
        {
            var state = new ReactiveProperty<ConnectionState>(ConnectionState.Disconnected);
            var keepConnected = new ReactiveProperty<bool>(true);
            var hub = new Mock<IMcpManagerHub>();
            hub.SetupGet(x => x.ConnectionState).Returns(state);
            hub.SetupGet(x => x.KeepConnected).Returns(keepConnected);
            hub.SetupGet(x => x.OnAuthorizationRejected).Returns(Observable.Empty<Unit>());
            hub.Setup(x => x.SetCapabilityRegistrationHandler(
                    It.IsAny<Func<CancellationToken, Task>>()))
                .Callback(capture);
            return hub;
        }

        static McpPlugin Plugin(IMcpManagerHub hub)
        {
            var tools = new Mock<IToolManager>();
            tools.SetupGet(x => x.OnToolsUpdated).Returns(Observable.Empty<Unit>());
            var prompts = new Mock<IPromptManager>();
            prompts.SetupGet(x => x.OnPromptsUpdated).Returns(Observable.Empty<Unit>());
            var resources = new Mock<IResourceManager>();
            resources.SetupGet(x => x.OnResourcesUpdated).Returns(Observable.Empty<Unit>());

            var manager = new Mock<IMcpManager>();
            manager.SetupGet(x => x.OnForceDisconnect).Returns(Observable.Empty<Unit>());
            manager.SetupGet(x => x.ToolManager).Returns(tools.Object);
            manager.SetupGet(x => x.PromptManager).Returns(prompts.Object);
            manager.SetupGet(x => x.ResourceManager).Returns(resources.Object);

            return new McpPlugin(
                NullLogger<McpPlugin>.Instance,
                manager.Object,
                hub,
                TestVersion,
                new Mock<ISkillFileGenerator>().Object,
                new SkillContentCollection());
        }

        static ResponseData Success()
            => new() { Status = ResponseStatus.Success };

        static async Task AwaitWithin(Task task, TimeSpan timeout)
        {
            var completed = await Task.WhenAny(task, Task.Delay(timeout));
            completed.ShouldBe(task, "lifecycle operation exceeded its bound");
            await task;
        }

        static async Task AwaitUntil(Func<bool> predicate, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (!predicate())
            {
                DateTime.UtcNow.ShouldBeLessThan(deadline, "lifecycle state did not converge within its bound");
                await Task.Delay(10);
            }
        }

        sealed class FastConnectionManager : ConnectionManager
        {
            readonly TimeSpan _initializationTimeout;

            public FastConnectionManager(
                IWebSocketConnectionProvider provider,
                TimeSpan initializationTimeout)
                : base(
                    NullLogger<ConnectionManager>.Instance,
                    TestVersion,
                    "http://127.0.0.1/test",
                    provider)
            {
                _initializationTimeout = initializationTimeout;
            }

            protected override TimeSpan RejectionThreshold => TimeSpan.FromMilliseconds(20);
            protected override TimeSpan ConnectionInitializationTimeout => _initializationTimeout;

            protected override Task WaitBeforeRetry(CancellationToken cancellationToken)
                => Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
        }

        sealed class TestConnector : BaseHubConnector
        {
            public int HandlerRegistrationCount { get; private set; }
            public int ConnectedCallbackCount { get; private set; }
            public Action? HandlerRegistered { get; set; }

            public TestConnector(IConnectionManager connectionManager)
                : base(NullLogger.Instance, TestVersion, connectionManager)
            {
            }

            protected override void RegisterServerHandlers(
                IConnectionManager connectionManager,
                CompositeDisposable disposables)
            {
                HandlerRegistrationCount++;
                HandlerRegistered?.Invoke();
                disposables.Add(new NoopDisposable());
            }

            protected override Task OnConnectedAsync(CancellationToken cancellationToken)
            {
                ConnectedCallbackCount++;
                return Task.CompletedTask;
            }
        }

        sealed class NoopDisposable : IDisposable
        {
            public void Dispose()
            {
            }
        }

        enum HandshakeBehavior
        {
            Stall,
            Compatible,
            CompatibleThenClose
        }

        sealed class LoopbackWebSocketServer : IAsyncDisposable
        {
            static readonly byte[] HeaderTerminator = { 13, 10, 13, 10 };
            readonly TcpListener _listener;
            readonly CancellationTokenSource _cancellation = new();
            readonly ConcurrentQueue<HandshakeBehavior> _behaviors;
            readonly ConcurrentBag<Task> _connections = new();
            readonly Task _acceptLoop;
            int _handshakeRequestCount;

            public Uri WebSocketUri { get; }
            public int HandshakeRequestCount => Volatile.Read(ref _handshakeRequestCount);
            public JsonSerializerOptions JsonOptions { get; } = new()
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            };

            public LoopbackWebSocketServer(params HandshakeBehavior[] behaviors)
            {
                JsonOptions.Converters.Add(
                    new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
                _behaviors = new ConcurrentQueue<HandshakeBehavior>(behaviors);
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
                WebSocketUri = new Uri($"ws://127.0.0.1:{port}/hub");
                _acceptLoop = AcceptLoop();
            }

            async Task AcceptLoop()
            {
                try
                {
                    while (!_cancellation.IsCancellationRequested)
                    {
                        var client = await _listener.AcceptTcpClientAsync(_cancellation.Token);
                        _behaviors.TryDequeue(out var behavior);
                        var connection = HandleConnection(client, behavior);
                        _connections.Add(connection);
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch (ObjectDisposedException)
                {
                }
                catch (SocketException) when (_cancellation.IsCancellationRequested)
                {
                }
            }

            async Task HandleConnection(TcpClient client, HandshakeBehavior behavior)
            {
                using (client)
                using (var stream = client.GetStream())
                {
                    try
                    {
                        var requestHeaders = await ReadHeaders(stream, _cancellation.Token);
                        var key = requestHeaders
                            .Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries)
                            .First(line => line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
                            .Substring("Sec-WebSocket-Key:".Length)
                            .Trim();
                        var accept = ComputeAcceptKey(key);
                        var response = Encoding.ASCII.GetBytes(
                            "HTTP/1.1 101 Switching Protocols\r\n" +
                            "Upgrade: websocket\r\n" +
                            "Connection: Upgrade\r\n" +
                            $"Sec-WebSocket-Accept: {accept}\r\n\r\n");
                        await stream.WriteAsync(response, _cancellation.Token);

                        while (!_cancellation.IsCancellationRequested)
                        {
                            var frame = await ReadFrame(stream, _cancellation.Token);
                            if (frame.Opcode == 8)
                                return;
                            if (frame.Opcode != 1)
                                continue;

                            using var document = JsonDocument.Parse(frame.Payload);
                            var root = document.RootElement;
                            if (!root.TryGetProperty("method", out var method) ||
                                method.GetString() != "PerformVersionHandshake")
                                continue;

                            Interlocked.Increment(ref _handshakeRequestCount);
                            if (behavior == HandshakeBehavior.Stall)
                                continue;

                            var id = root.GetProperty("id").GetString()!;
                            var handshake = new VersionHandshakeResponse
                            {
                                ApiVersion = TestVersion.Api,
                                ServerVersion = TestVersion.Plugin,
                                Compatible = true,
                                Message = "compatible",
                                IsConnectionError = false
                            };
                            var result = JsonSerializer.SerializeToElement(handshake, JsonOptions);
                            var payload = WsEnvelope.SerializeResponse(new WsResponse
                            {
                                Id = id,
                                Result = result
                            }, JsonOptions);
                            await WriteFrame(stream, 1, payload, _cancellation.Token);

                            if (behavior == HandshakeBehavior.CompatibleThenClose)
                            {
                                await Task.Delay(TimeSpan.FromMilliseconds(100), _cancellation.Token);
                                await WriteFrame(stream, 8, Array.Empty<byte>(), _cancellation.Token);
                                return;
                            }
                        }
                    }
                    catch (Exception ex) when (
                        ex is IOException ||
                        ex is OperationCanceledException ||
                        ex is ObjectDisposedException ||
                        ex is SocketException)
                    {
                    }
                }
            }

            static async Task<string> ReadHeaders(NetworkStream stream, CancellationToken cancellationToken)
            {
                var bytes = new List<byte>();
                var one = new byte[1];
                while (bytes.Count < 16 * 1024)
                {
                    var read = await stream.ReadAsync(one, cancellationToken);
                    if (read == 0)
                        throw new IOException("Client closed during the WebSocket handshake.");
                    bytes.Add(one[0]);
                    if (bytes.Count >= HeaderTerminator.Length &&
                        bytes.Skip(bytes.Count - HeaderTerminator.Length).SequenceEqual(HeaderTerminator))
                        return Encoding.ASCII.GetString(bytes.ToArray());
                }
                throw new IOException("WebSocket request headers exceeded the test bound.");
            }

            static string ComputeAcceptKey(string key)
            {
                using var sha1 = SHA1.Create();
                var bytes = Encoding.ASCII.GetBytes(
                    key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11");
                return Convert.ToBase64String(sha1.ComputeHash(bytes));
            }

            static async Task<(int Opcode, byte[] Payload)> ReadFrame(
                NetworkStream stream,
                CancellationToken cancellationToken)
            {
                var header = await ReadExactly(stream, 2, cancellationToken);
                var opcode = header[0] & 0x0f;
                var masked = (header[1] & 0x80) != 0;
                ulong length = (uint)(header[1] & 0x7f);
                if (length == 126)
                {
                    var extended = await ReadExactly(stream, 2, cancellationToken);
                    length = (uint)((extended[0] << 8) | extended[1]);
                }
                else if (length == 127)
                {
                    var extended = await ReadExactly(stream, 8, cancellationToken);
                    length = 0;
                    for (var i = 0; i < extended.Length; i++)
                        length = (length << 8) | extended[i];
                }

                if (length > 1024 * 1024)
                    throw new IOException("WebSocket test frame exceeded the one MiB bound.");

                var mask = masked
                    ? await ReadExactly(stream, 4, cancellationToken)
                    : Array.Empty<byte>();
                var payload = await ReadExactly(stream, checked((int)length), cancellationToken);
                if (masked)
                {
                    for (var i = 0; i < payload.Length; i++)
                        payload[i] ^= mask[i % 4];
                }
                return (opcode, payload);
            }

            static async Task<byte[]> ReadExactly(
                NetworkStream stream,
                int count,
                CancellationToken cancellationToken)
            {
                var bytes = new byte[count];
                var offset = 0;
                while (offset < count)
                {
                    var read = await stream.ReadAsync(
                        bytes.AsMemory(offset, count - offset),
                        cancellationToken);
                    if (read == 0)
                        throw new IOException("Client closed the WebSocket test connection.");
                    offset += read;
                }
                return bytes;
            }

            static async Task WriteFrame(
                NetworkStream stream,
                int opcode,
                byte[] payload,
                CancellationToken cancellationToken)
            {
                using var frame = new MemoryStream();
                frame.WriteByte((byte)(0x80 | opcode));
                if (payload.Length < 126)
                {
                    frame.WriteByte((byte)payload.Length);
                }
                else if (payload.Length <= ushort.MaxValue)
                {
                    frame.WriteByte(126);
                    frame.WriteByte((byte)(payload.Length >> 8));
                    frame.WriteByte((byte)payload.Length);
                }
                else
                {
                    frame.WriteByte(127);
                    for (var shift = 56; shift >= 0; shift -= 8)
                        frame.WriteByte((byte)((ulong)payload.Length >> shift));
                }
                frame.Write(payload, 0, payload.Length);
                await stream.WriteAsync(frame.ToArray(), cancellationToken);
            }

            public async ValueTask DisposeAsync()
            {
                _cancellation.Cancel();
                _listener.Stop();
                try { await _acceptLoop; }
                catch { }
                try { await Task.WhenAll(_connections.ToArray()); }
                catch { }
                _cancellation.Dispose();
            }
        }
    }
}
