using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;
using Xunit;

namespace com.AtelierAI.Uco.Framework.Tests.Network.Connection
{
    public sealed class ConnectionManagerDiagnosticsTests
    {
        static readonly Common.Version TestVersion = new()
        {
            Api = "1.0.0",
            Plugin = "1.0.0",
            Environment = "test"
        };

        [Fact]
        public async Task ProviderUnavailable_TwentyCycles_AreTerminalAttributedAndLeakFree()
        {
            var provider = Provider();
            provider.Setup(x => x.CreateConnectionAsync(It.IsAny<string>()))
                .ReturnsAsync((null!, new Uri("ws://127.0.0.1:1/hub")));
            await using var manager = Manager(provider.Object);
            var attemptIds = new HashSet<long>();

            for (var cycle = 0; cycle < 20; cycle++)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                (await manager.Connect(timeout.Token)).ShouldBeFalse();
                var diagnostic = manager.LastAttempt;
                diagnostic.Terminal.ShouldBeTrue();
                diagnostic.Stage.ShouldBe("transport-failed");
                diagnostic.AttemptId.ShouldBeGreaterThan(0);
                diagnostic.Generation.ShouldBe(manager.ConnectionGeneration);
                diagnostic.RetryCount.ShouldBe(0);
                diagnostic.CompletedAtUtc.ShouldNotBeNullOrEmpty();
                diagnostic.RootCause.ShouldContain("did not create");
                attemptIds.Add(diagnostic.AttemptId).ShouldBeTrue("every cycle needs a distinct attempt id");
                manager.ActiveAttemptId.ShouldBe(0, "terminal attempts must not remain active");
            }

            provider.Verify(x => x.CreateConnectionAsync(It.IsAny<string>()), Times.Exactly(20));
        }

        [Fact]
        public async Task UnreachableEndpoint_TwentyCycles_AreBoundedAndReleaseTheGate()
        {
            var port = ReserveUnusedLoopbackPort();
            var endpoint = new Uri($"ws://127.0.0.1:{port}/hub");
            var provider = Provider();
            provider.Setup(x => x.CreateConnectionAsync(It.IsAny<string>()))
                .ReturnsAsync(() =>
                {
                    var socket = new ClientWebSocket();
                    socket.Options.Proxy = null;
                    return (socket, endpoint);
                });
            await using var manager = Manager(provider.Object);
            var attemptIds = new HashSet<long>();

            for (var cycle = 0; cycle < 20; cycle++)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
                var connect = manager.Connect(timeout.Token);
                await AwaitWithin(connect, TimeSpan.FromSeconds(2));

                (await connect).ShouldBeFalse();
                manager.LastAttempt.Terminal.ShouldBeTrue();
                (manager.LastAttempt.Stage == "transport-failed" ||
                    manager.LastAttempt.Stage == "cancelled").ShouldBeTrue(
                    "an unreachable endpoint must terminate as a transport failure or bounded cancellation");
                manager.LastAttempt.RootCause.ShouldNotBeNullOrEmpty();
                attemptIds.Add(manager.LastAttempt.AttemptId)
                    .ShouldBeTrue("every real transport cycle needs a distinct attempt id");
                manager.ActiveAttemptId.ShouldBe(0, "failed sockets must not leave an active attempt");
            }

            provider.Verify(x => x.CreateConnectionAsync(It.IsAny<string>()), Times.AtLeast(20));
        }

        static int ReserveUnusedLoopbackPort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                return ((IPEndPoint)listener.LocalEndpoint).Port;
            }
            finally
            {
                listener.Stop();
            }
        }

        [Fact]
        public async Task ProviderException_TwentyCycles_BoundsRootCauseAndReleasesTheGate()
        {
            var provider = Provider();
            var longCause = new string('x', 700);
            provider.Setup(x => x.CreateConnectionAsync(It.IsAny<string>()))
                .ThrowsAsync(new InvalidOperationException(longCause));
            await using var manager = Manager(provider.Object);

            for (var cycle = 0; cycle < 20; cycle++)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                (await manager.Connect(timeout.Token)).ShouldBeFalse();
                manager.LastAttempt.Stage.ShouldBe("transport-failed");
                manager.LastAttempt.RootCause!.Length.ShouldBeLessThanOrEqualTo(515);
                manager.ActiveAttemptId.ShouldBe(0);
            }
        }

        [Fact]
        public async Task DirectConcurrentConnect_TwentyCycles_CancelsStaleWorkAndKeepsGateBalanced()
        {
            for (var cycle = 0; cycle < 20; cycle++)
            {
                var provider = Provider();
                var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var calls = 0;
                provider.Setup(x => x.CreateConnectionAsync(It.IsAny<string>()))
                    .Returns(async () =>
                    {
                        if (Interlocked.Increment(ref calls) == 1)
                        {
                            started.TrySetResult(true);
                            await release.Task;
                            return (new ClientWebSocket(), new Uri("ws://127.0.0.1:1/hub"));
                        }
                        return (null!, new Uri("ws://127.0.0.1:1/hub"));
                    });
                await using var manager = Manager(provider.Object);

                using var firstTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                var stale = manager.Connect(firstTimeout.Token);
                await AwaitWithin(started.Task, TimeSpan.FromSeconds(1));
                using var replacementTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                var replacement = manager.Connect(replacementTimeout.Token);

                await AwaitWithin(Task.WhenAll(stale, replacement), TimeSpan.FromSeconds(2));
                (await stale).ShouldBeFalse();
                (await replacement).ShouldBeFalse();
                firstTimeout.IsCancellationRequested.ShouldBeFalse(
                    "the manager-owned token, not the first caller token, must cancel stale work");
                calls.ShouldBe(2, "replacement must progress after stale work settles");
                manager.ActiveAttemptId.ShouldBe(0);
                manager.LastAttempt.Terminal.ShouldBeTrue();

                // Complete the deliberately non-cooperative provider after ownership has
                // moved on; its late socket must only be observed and disposed.
                release.TrySetResult(true);
            }
        }

        static Mock<IWebSocketConnectionProvider> Provider()
        {
            var provider = new Mock<IWebSocketConnectionProvider>();
            provider.SetupGet(x => x.JsonSerializerOptions).Returns(new JsonSerializerOptions());
            return provider;
        }

        static ConnectionManager Manager(IWebSocketConnectionProvider provider)
            => new(NullLogger<ConnectionManager>.Instance, TestVersion,
                "http://127.0.0.1:1/hub", provider);

        static async Task AwaitWithin(Task task, TimeSpan timeout)
        {
            var completed = await Task.WhenAny(task, Task.Delay(timeout));
            completed.ShouldBe(task, "connection cycle exceeded its bound");
            await task;
        }
    }
}
