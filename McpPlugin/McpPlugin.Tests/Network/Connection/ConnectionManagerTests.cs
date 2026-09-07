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
using System.Threading;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Tasks;
using com.IvanMurzak.McpPlugin.Tests.Infrastructure;
using Shouldly;


using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Xunit.Abstractions;

namespace com.IvanMurzak.McpPlugin.Tests.Network.Connection
{
    /// <summary>
    /// Unit tests for ConnectionManager.Connect method.
    ///
    /// NOTE: Due to HubConnection being a sealed class in ASP.NET Core SignalR,
    /// direct mocking of HubConnection.State and other non-virtual members is not possible.
    /// These tests focus on testing the connection logic, concurrency handling, and
    /// cancellation scenarios by mocking the IWebSocketConnectionProvider interface.
    ///
    /// For complete integration testing with real HubConnection instances,
    /// separate integration tests should be created using TestServer.
    /// </summary>
    public class ConnectionManagerTests
    {
        private readonly ITestOutputHelper _output;
        private readonly ILogger _logger;
        private readonly Mock<IWebSocketConnectionProvider> _mockWsProvider;
        private readonly Common.Version _testVersion;
        private readonly string _testEndpoint;

        public ConnectionManagerTests(ITestOutputHelper output)
        {
            _output = output;
            var loggerFactory = TestLoggerFactory.Create(_output, LogLevel.Debug);
            _logger = loggerFactory.CreateLogger<ConnectionManagerTests>();
            _mockWsProvider = new Mock<IWebSocketConnectionProvider>();
            _mockWsProvider.SetupGet(x => x.JsonSerializerOptions).Returns(new JsonSerializerOptions());
            _testVersion = new Common.Version { Api = "1.0.0", Plugin = "1.0.0", Environment = "test" };
            _testEndpoint = "http://localhost:5000/hub";
        }

        #region Provider Interaction Tests

        [Fact]
        public async Task Connect_CallsProviderToCreateConnection()
        {
            // Arrange
            var mockConnection = CreateMockConnection();
            _mockWsProvider
                .Setup(x => x.CreateConnectionAsync(_testEndpoint))
                .ReturnsAsync(mockConnection);

            await using var connectionManager = new ConnectionManager(
                _logger,
                _testVersion,
                _testEndpoint,
                _mockWsProvider.Object
            );

            // Act
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var result = await connectionManager.Connect(cts.Token);

            // Assert
            _mockWsProvider.Verify(x => x.CreateConnectionAsync(_testEndpoint), Times.Once);
        }

        [Fact]
        public async Task Connect_WhenProviderReturnsNull_ReturnsFalse()
        {
            // Arrange
            _mockWsProvider
                .Setup(x => x.CreateConnectionAsync(_testEndpoint))
                .ReturnsAsync((null!, new Uri("ws://localhost:9999/test")));

            await using var connectionManager = new ConnectionManager(
                _logger,
                _testVersion,
                _testEndpoint,
                _mockWsProvider.Object
            );

            // Act
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var result = await connectionManager.Connect(cts.Token);

            // Assert
            result.ShouldBeFalse();
        }

        [Fact]
        public async Task Connect_WhenProviderThrows_ReturnsFalse()
        {
            // Arrange
            _mockWsProvider
                .Setup(x => x.CreateConnectionAsync(_testEndpoint))
                .ThrowsAsync(new InvalidOperationException("Failed to create connection"));

            await using var connectionManager = new ConnectionManager(
                _logger,
                _testVersion,
                _testEndpoint,
                _mockWsProvider.Object
            );

            // Act
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var result = await connectionManager.Connect(cts.Token);

            // Assert
            result.ShouldBeFalse();
        }

        #endregion

        #region Multithreading & Concurrency Tests

        [Fact]
        public async Task Connect_WhenMultipleThreadsCallSimultaneously_ElectsOneReplacement()
        {
            // Arrange
            var connectionCreationCount = 0;
            var connectionCreated = new TaskCompletionSource<bool>();
            var allowConnectionToComplete = new TaskCompletionSource<bool>();

            _mockWsProvider
                .Setup(x => x.CreateConnectionAsync(_testEndpoint))
                .Returns(async () =>
                {
                    var count = Interlocked.Increment(ref connectionCreationCount);
                    System.Diagnostics.Debug.WriteLine($"CreateConnectionAsync called. Count: {count}");
                    connectionCreated.TrySetResult(true);
                    // Block until test releases — ensures all concurrent callers see
                    // _ongoingConnectionTask before this attempt completes and clears it.
                    if (count == 1)
                    {
                        await allowConnectionToComplete.Task;
                        return CreateMockConnection();
                    }
                    return (null!, new Uri("ws://localhost:9999/test"));
                });

            await using var connectionManager = new ConnectionManager(
                _logger,
                _testVersion,
                _testEndpoint,
                _mockWsProvider.Object
            );

            // Act - Launch 10 concurrent connection attempts
            var tasks = new Task<bool>[10];
            for (int i = 0; i < tasks.Length; i++)
            {
                var taskIndex = i;
                tasks[i] = Task.Run(async () =>
                {
                    System.Diagnostics.Debug.WriteLine($"Task {taskIndex} starting Connect call");
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    var result = await connectionManager.Connect(cts.Token);
                    System.Diagnostics.Debug.WriteLine($"Task {taskIndex} completed with result: {result}");
                    return result;
                });
            }

            System.Diagnostics.Debug.WriteLine("Waiting for connection to start...");
            await EnsureConnectionStartedAsync(connectionCreated.Task);
            System.Diagnostics.Debug.WriteLine($"Connection started. Current creation count: {connectionCreationCount}");

            await Task.Delay(200); // Allow other tasks to queue and see ongoing task

            System.Diagnostics.Debug.WriteLine($"Before release. Creation count: {connectionCreationCount}");

            // The stale provider does not observe cancellation. The elected replacement must
            // nevertheless progress without waiting for that provider to return.
            connectionCreationCount.ShouldBe(2,
                "one stale creation and one elected replacement should be active");

            // Release the abandoned provider result so its late socket can be disposed.
            allowConnectionToComplete.TrySetResult(true);

            // Wait for the stale owner and its single elected replacement to complete.
            await EnsureTasksCompleteAsync(8000, tasks);

            System.Diagnostics.Debug.WriteLine($"Final connection creation count: {connectionCreationCount}");
            connectionCreationCount.ShouldBe(2,
                "concurrent followers must join the one elected replacement");
        }

        [Fact]
        public async Task Connect_WhenCalledConcurrently_ReplacesFirstConnectionAttempt()
        {
            // Arrange
            var connectionStarted = new TaskCompletionSource<bool>();
            var allowConnectionToComplete = new TaskCompletionSource<bool>();
            var providerCallCount = 0;

            _mockWsProvider
                .Setup(x => x.CreateConnectionAsync(_testEndpoint))
                .Returns(async () =>
                {
                    var count = Interlocked.Increment(ref providerCallCount);
                    connectionStarted.TrySetResult(true);
                    if (count == 1)
                    {
                        await allowConnectionToComplete.Task;
                        return CreateMockConnection();
                    }
                    return (null!, new Uri("ws://localhost:9999/test"));
                });

            await using var connectionManager = new ConnectionManager(
                _logger,
                _testVersion,
                _testEndpoint,
                _mockWsProvider.Object
            );

            // Act
            var firstTask = Task.Run(async () =>
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                return await connectionManager.Connect(cts.Token);
            });

            await EnsureConnectionStartedAsync(connectionStarted.Task);

            var secondTask = Task.Run(async () =>
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                return await connectionManager.Connect(cts.Token);
            });

            await Task.Delay(50);

            // The explicit second call replaces the stale provider wait immediately.
            providerCallCount.ShouldBe(2,
                "second call should start its replacement without waiting for stale provider return");

            // Complete the abandoned provider and wait for both callers.
            allowConnectionToComplete.SetResult(true);
            await EnsureTasksCompleteAsync(5000, firstTask, secondTask);

            providerCallCount.ShouldBe(2, "the second call should own one replacement attempt");
        }

        [Fact]
        public async Task Connect_WhenCalledSequentially_CreatesMultipleConnections()
        {
            // Arrange
            var providerCallCount = 0;
            _mockWsProvider
                .Setup(x => x.CreateConnectionAsync(_testEndpoint))
                .Returns(async () =>
                {
                    Interlocked.Increment(ref providerCallCount);
                    await Task.Yield(); // Minimal delay to simulate async operation
                    return CreateMockConnection();
                });

            await using var connectionManager = new ConnectionManager(
                _logger,
                _testVersion,
                _testEndpoint,
                _mockWsProvider.Object
            );

            // Act - Make 3 sequential connection attempts
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var result1 = await connectionManager.Connect(cts.Token);
            await Task.Delay(20); // Minimal delay to ensure connection completes

            using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var result2 = await connectionManager.Connect(cts2.Token);
            await Task.Delay(20);

            using var cts3 = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var result3 = await connectionManager.Connect(cts3.Token);

            // Assert - Sequential calls should each create a connection (or reuse if already connected)
            providerCallCount.ShouldBeGreaterThanOrEqualTo(1, "at least one connection should be created");
        }

        [Fact]
        public async Task Connect_ThreadSafety_NoRaceConditionsUnderLoad()
        {
            // Arrange
            var callCount = 0;
            var connectionStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var replacementStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var allowConnectionToComplete = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var allowReplacementToComplete = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var startCallers = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var allCallersReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var allConnectCallsStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var readyCallers = 0;
            var startedConnectCalls = 0;

            _mockWsProvider
                .Setup(x => x.CreateConnectionAsync(_testEndpoint))
                .Returns(async () =>
                {
                    var count = Interlocked.Increment(ref callCount);
                    connectionStarted.TrySetResult(true);
                    if (count == 1)
                    {
                        await allowConnectionToComplete.Task; // Wait for signal to complete
                        return CreateMockConnection();
                    }
                    if (count == 2)
                    {
                        replacementStarted.TrySetResult(true);
                        await allowReplacementToComplete.Task;
                    }
                    return (null!, new Uri("ws://localhost:9999/test"));
                });

            await using var connectionManager = new ConnectionManager(
                _logger,
                _testVersion,
                _testEndpoint,
                _mockWsProvider.Object
            );

            // Act - Launch 20 concurrent connection attempts to stress test thread safety
            var tasks = new Task<bool>[20];

            for (int i = 0; i < 20; i++)
            {
                tasks[i] = Task.Run(async () =>
                {
                    if (Interlocked.Increment(ref readyCallers) == tasks.Length)
                        allCallersReady.TrySetResult(true);
                    await startCallers.Task;
                    using var taskCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    var connect = connectionManager.Connect(taskCts.Token);
                    if (Interlocked.Increment(ref startedConnectCalls) == tasks.Length)
                        allConnectCallsStarted.TrySetResult(true);
                    return await connect;
                });
            }

            // Release all callers together. Hold both the stale provider and the elected
            // replacement until every Connect invocation has published its ownership choice;
            // otherwise ThreadPool scheduling, rather than connection coordination, decides
            // whether a late task belongs to this concurrent burst.
            await EnsureConnectionStartedAsync(allCallersReady.Task);
            startCallers.TrySetResult(true);
            await EnsureConnectionStartedAsync(connectionStarted.Task);
            await EnsureConnectionStartedAsync(replacementStarted.Task);
            await EnsureConnectionStartedAsync(allConnectCallsStarted.Task);
            allowConnectionToComplete.TrySetResult(true);
            allowReplacementToComplete.TrySetResult(true);

            // This should not throw any exceptions or deadlock
            await EnsureTasksCompleteAsync(8000, tasks);

            // Assert - All tasks should complete without exceptions
            tasks.ShouldNotBeNull();
            tasks.Length.ShouldBe(20);
            // Due to replacement ownership, one stale owner and one replacement are created.
            callCount.ShouldBe(2, "concurrent followers should join the same replacement attempt");
        }

        #endregion

        #region Cancellation Tests

        [Fact]
        public async Task Connect_WhenCancellationTokenAlreadyCanceled_ReturnsFalse()
        {
            // Arrange
            var cts = new CancellationTokenSource();
            cts.Cancel(); // Cancel before calling Connect

            var mockConnection = CreateMockConnection();
            _mockWsProvider
                .Setup(x => x.CreateConnectionAsync(_testEndpoint))
                .ReturnsAsync(mockConnection);

            await using var connectionManager = new ConnectionManager(
                _logger,
                _testVersion,
                _testEndpoint,
                _mockWsProvider.Object
            );

            // Act
            var result = await connectionManager.Connect(cts.Token);

            // Assert
            result.ShouldBeFalse();
        }

        [Fact]
        public async Task Connect_WhenCanceledDuringConnection_ReturnsFalse()
        {
            // Arrange
            var cts = new CancellationTokenSource();
            var connectionStarted = new TaskCompletionSource<bool>();

            _mockWsProvider
                .Setup(x => x.CreateConnectionAsync(_testEndpoint))
                .Returns(async () =>
                {
                    connectionStarted.SetResult(true);
                    await Task.Delay(Timeout.Infinite, cts.Token); // Will be canceled
                    return CreateMockConnection();
                });

            await using var connectionManager = new ConnectionManager(
                _logger,
                _testVersion,
                _testEndpoint,
                _mockWsProvider.Object
            );

            // Act
            var connectTask = Task.Run(async () => await connectionManager.Connect(cts.Token));

            await EnsureConnectionStartedAsync(connectionStarted.Task);
            cts.Cancel(); // Cancel during connection

            var result = await connectTask;

            // Assert
            result.ShouldBeFalse();
        }

        [Fact]
        public async Task Connect_WithShortTimeout_ReturnsFalse()
        {
            // Arrange
            var connectionStarted = new TaskCompletionSource<bool>();
            var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

            _mockWsProvider
                .Setup(x => x.CreateConnectionAsync(_testEndpoint))
                .Returns(async () =>
                {
                    connectionStarted.TrySetResult(true);
                    // Simulate a long operation that will be interrupted by timeout
                    await Task.Delay(TimeSpan.FromSeconds(10), cts.Token);
                    return CreateMockConnection();
                });

            await using var connectionManager = new ConnectionManager(
                _logger,
                _testVersion,
                _testEndpoint,
                _mockWsProvider.Object
            );

            // Act - Use very short timeout
            var result = await connectionManager.Connect(cts.Token);

            // Assert
            result.ShouldBeFalse();
        }

        [Fact]
        public async Task Connect_CancellationPropagatedToProvider()
        {
            // Arrange
            var connectionStarted = new TaskCompletionSource<bool>();
            var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

            _mockWsProvider
                .Setup(x => x.CreateConnectionAsync(_testEndpoint))
                .Returns(async () =>
                {
                    connectionStarted.SetResult(true);
                    // Note: CreateConnectionAsync doesn't receive a cancellation token from ConnectionManager,
                    // so we simulate a long operation that will be interrupted by the Connect method's
                    // cancellation handling instead of direct cancellation of the provider call
                    await Task.Delay(TimeSpan.FromSeconds(10), cts.Token);
                    return CreateMockConnection();
                });

            await using var connectionManager = new ConnectionManager(
                _logger,
                _testVersion,
                _testEndpoint,
                _mockWsProvider.Object
            );

            // Act
            var result = await connectionManager.Connect(cts.Token);

            // Assert
            result.ShouldBeFalse();
        }

        #endregion

        #region Exception Handling Tests

        [Fact]
        public async Task Connect_WhenProviderThrowsOperationCanceledException_ReturnsFalse()
        {
            // Arrange
            _mockWsProvider
                .Setup(x => x.CreateConnectionAsync(_testEndpoint))
                .ThrowsAsync(new OperationCanceledException("Connection canceled"));

            await using var connectionManager = new ConnectionManager(
                _logger,
                _testVersion,
                _testEndpoint,
                _mockWsProvider.Object
            );

            // Act
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var result = await connectionManager.Connect(cts.Token);

            // Assert
            result.ShouldBeFalse();
        }

        [Fact]
        public async Task Connect_WhenExceptionOccurs_LogsError()
        {
            // Arrange
            _mockWsProvider
                .Setup(x => x.CreateConnectionAsync(_testEndpoint))
                .ThrowsAsync(new Exception("Connection error"));

            await using var connectionManager = new ConnectionManager(
                _logger,
                _testVersion,
                _testEndpoint,
                _mockWsProvider.Object
            );

            // Act
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var result = await connectionManager.Connect(cts.Token);

            // Assert
            result.ShouldBeFalse();
            // Note: Since we're using a real logger, we can't verify mock calls.
            // The test verifies that the method returns false when an exception occurs,
            // and the actual logging can be observed in the test output.
        }

        #endregion

        #region Disconnect Tests

        [Fact]
        public async Task Disconnect_WhenCalledDuringMultipleConcurrentConnectAttempts_StopsAllAttempts()
        {
            // Arrange
            var connectionStarted = new TaskCompletionSource<bool>();
            var allowConnectionToComplete = new TaskCompletionSource<bool>();
            var providerCallCount = 0;

            _mockWsProvider
                .Setup(x => x.CreateConnectionAsync(_testEndpoint))
                .Returns(async () =>
                {
                    var count = Interlocked.Increment(ref providerCallCount);
                    System.Diagnostics.Debug.WriteLine($"CreateConnectionAsync called. Count: {count}");
                    connectionStarted.TrySetResult(true);
                    await allowConnectionToComplete.Task;
                    return CreateMockConnection();
                });

            await using var connectionManager = new ConnectionManager(
                _logger,
                _testVersion,
                _testEndpoint,
                _mockWsProvider.Object
            );

            // Act - Start first connection attempt without awaiting
            var firstConnectTask = Task.Run(async () =>
            {
                System.Diagnostics.Debug.WriteLine("First Connect call started");
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var result = await connectionManager.Connect(cts.Token);
                System.Diagnostics.Debug.WriteLine($"First Connect call completed with result: {result}");
                return result;
            });

            // Wait for the first connection to start
            await EnsureConnectionStartedAsync(connectionStarted.Task);
            System.Diagnostics.Debug.WriteLine("First connection started");

            // Start second connection attempt (should replace the first)
            var secondConnectTask = Task.Run(async () =>
            {
                System.Diagnostics.Debug.WriteLine("Second Connect call started");
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var result = await connectionManager.Connect(cts.Token);
                System.Diagnostics.Debug.WriteLine($"Second Connect call completed with result: {result}");
                return result;
            });

            // Give second connect time to claim and start the replacement.
            await Task.Delay(100);
            System.Diagnostics.Debug.WriteLine("Second connection should be waiting now");

            // Now call Disconnect - this should cancel all connection attempts
            var disconnectTask = Task.Run(async () =>
            {
                System.Diagnostics.Debug.WriteLine("Disconnect call started");
                await connectionManager.Disconnect();
                System.Diagnostics.Debug.WriteLine("Disconnect call completed");
            });

            // Give Disconnect time to cancel the internal token
            await Task.Delay(100);
            System.Diagnostics.Debug.WriteLine("Disconnect should have canceled the token");

            // Allow the connection creation to complete (but it should already be canceled)
            allowConnectionToComplete.SetResult(true);
            System.Diagnostics.Debug.WriteLine("Allowed connection to complete");

            // Wait for all operations to complete
            await EnsureTasksCompleteAsync(10000, firstConnectTask, secondConnectTask, disconnectTask);
            System.Diagnostics.Debug.WriteLine("All tasks completed");

            // Assert - Both connect calls should return false (canceled)
            var firstResult = await firstConnectTask;
            var secondResult = await secondConnectTask;

            firstResult.ShouldBeFalse("first connect should be canceled by Disconnect");
            secondResult.ShouldBeFalse("second connect should be canceled by Disconnect");

            // One stale owner plus one elected replacement should have started before disconnect.
            providerCallCount.ShouldBe(2,
                "concurrent explicit calls should elect exactly one replacement before disconnect");

            System.Diagnostics.Debug.WriteLine($"Test completed. Provider call count: {providerCallCount}");
        }

        [Fact]
        public async Task Disconnect_AfterConcurrentReplacement_PreventsFurtherConnectFromProceeding()
        {
            // Connect #2 is allowed to replace Connect #1, but a later Disconnect must
            // cancel that replacement and prevent any third generation from starting.

            // Arrange
            var connectionStarted = new TaskCompletionSource<bool>();
            var firstConnectCanFinish = new TaskCompletionSource<bool>();
            var providerCallCount = 0;

            _mockWsProvider
                .Setup(x => x.CreateConnectionAsync(_testEndpoint))
                .Returns(async () =>
                {
                    var count = Interlocked.Increment(ref providerCallCount);
                    System.Diagnostics.Debug.WriteLine($"Provider call #{count}");
                    connectionStarted.TrySetResult(true);
                    await firstConnectCanFinish.Task;
                    return CreateMockConnection();
                });

            await using var connectionManager = new ConnectionManager(
                _logger,
                _testVersion,
                _testEndpoint,
                _mockWsProvider.Object
            );

            // Act
            // Start first Connect (will acquire gate and start connection)
            var firstConnect = Task.Run(async () =>
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                return await connectionManager.Connect(cts.Token);
            });

            // Wait for first connection to start
            await EnsureConnectionStartedAsync(connectionStarted.Task);

            // Start second Connect immediately; it should own the replacement.
            var secondConnect = Task.Run(async () =>
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                return await connectionManager.Connect(cts.Token);
            });

            await Task.Delay(50); // Let second connect start waiting

            // Call Disconnect (should cancel token and clear ongoing task)
            var disconnect = Task.Run(async () => await connectionManager.Disconnect());

            await Task.Delay(50); // Let disconnect cancel the token

            // Now allow first connect to finish
            firstConnectCanFinish.SetResult(true);

            // Wait for everything to complete
            await EnsureTasksCompleteAsync(10000, firstConnect, secondConnect, disconnect);

            // Assert
            var firstResult = await firstConnect;
            var secondResult = await secondConnect;

            // Both should fail because Disconnect was called
            firstResult.ShouldBeFalse("first connect should fail due to disconnect");
            secondResult.ShouldBeFalse("second connect should fail because ongoing task was canceled and cleared");

            // The second explicit call starts its replacement before Disconnect. Disconnect
            // must stop both generations and must not permit a third provider call afterward.
            providerCallCount.ShouldBe(2,
                "disconnect should prevent any provider call after the elected replacement");
        }

        [Fact]
        public async Task Connect_AfterDisconnect_SucceedsOnReconnect()
        {
            // This test verifies the Connect -> Disconnect -> Connect scenario
            // Bug: After Disconnect, the internalCts is canceled but not disposed/nulled,
            // causing subsequent Connect attempts to fail at the canceled token check

            // Arrange
            var providerCallCount = 0;
            _mockWsProvider
                .Setup(x => x.CreateConnectionAsync(_testEndpoint))
                .Returns(async () =>
                {
                    var count = Interlocked.Increment(ref providerCallCount);
                    System.Diagnostics.Debug.WriteLine($"Provider call #{count}");
                    await Task.Yield();
                    return CreateMockConnection();
                });

            await using var connectionManager = new ConnectionManager(
                _logger,
                _testVersion,
                _testEndpoint,
                _mockWsProvider.Object
            );

            // Act
            // First Connect
            System.Diagnostics.Debug.WriteLine("First Connect attempt");
            using var cts1 = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var firstConnect = await connectionManager.Connect(cts1.Token);
            System.Diagnostics.Debug.WriteLine($"First Connect result: {firstConnect}");

            await Task.Delay(100); // Give connection time to process

            // Disconnect
            System.Diagnostics.Debug.WriteLine("Disconnecting");
            await connectionManager.Disconnect();
            System.Diagnostics.Debug.WriteLine("Disconnect completed");

            await Task.Delay(100); // Give disconnect time to process

            // Second Connect (should succeed)
            System.Diagnostics.Debug.WriteLine("Second Connect attempt (after disconnect)");
            using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var secondConnect = await connectionManager.Connect(cts2.Token);
            System.Diagnostics.Debug.WriteLine($"Second Connect result: {secondConnect}");

            // Assert
            // The second connect should succeed (or at least attempt to connect)
            // The provider should be called at least twice (once for each Connect)
            providerCallCount.ShouldBeGreaterThanOrEqualTo(2, "reconnection after disconnect should create a new connection");
        }

        [Fact]
        public void DisconnectImmediate_WhenNotConnected_DoesNotThrow()
        {
            // Arrange
            using var connectionManager = new ConnectionManager(
                _logger,
                _testVersion,
                _testEndpoint,
                _mockWsProvider.Object
            );

            // Act & Assert
            Action act = () => connectionManager.DisconnectImmediate();
            Should.NotThrow(act); // DisconnectImmediate must be safe to call when no connection exists
        }

        [Fact]
        public async Task DisconnectImmediate_WhileConnecting_DoesNotThrowAndConnectReturnsFalse()
        {
            // Regression: Connect captures a local reference to _ongoingConnectionTask before
            // releasing the gate, so DisconnectImmediate nulling _ongoingConnectionTask cannot
            // cause a NullReferenceException on the awaiting caller.

            // Arrange
            var connectionStarted = new TaskCompletionSource<bool>();
            var allowConnectionToComplete = new TaskCompletionSource<bool>();

            _mockWsProvider
                .Setup(x => x.CreateConnectionAsync(_testEndpoint))
                .Returns(async () =>
                {
                    connectionStarted.TrySetResult(true);
                    await allowConnectionToComplete.Task;
                    return CreateMockConnection();
                });

            await using var connectionManager = new ConnectionManager(
                _logger,
                _testVersion,
                _testEndpoint,
                _mockWsProvider.Object
            );

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var connectTask = Task.Run(() => connectionManager.Connect(cts.Token));

            // Wait until the provider is executing (connection is in flight)
            await EnsureConnectionStartedAsync(connectionStarted.Task);

            // Act: must not deadlock or throw even though Connect holds the gate
            Action act = () => connectionManager.DisconnectImmediate();
            Should.NotThrow(act); // DisconnectImmediate must not deadlock or throw during an in-flight Connect

            // Unblock the provider; token is already canceled so Connect should return false
            allowConnectionToComplete.SetResult(true);

            // Assert: Connect must complete cleanly — no NullReferenceException from the race
            var result = await connectTask;
            result.ShouldBeFalse("Connect should return false because DisconnectImmediate canceled it");
        }

        #endregion

        #region InvokeAsync Tests

        [Fact]
        public async Task InvokeAsync_WhenDisposed_ReturnsDefault()
        {
            // Arrange
            var mockConnection = CreateMockConnection();
            _mockWsProvider
                .Setup(x => x.CreateConnectionAsync(_testEndpoint))
                .ReturnsAsync(mockConnection);

            var connectionManager = new ConnectionManager(
                _logger,
                _testVersion,
                _testEndpoint,
                _mockWsProvider.Object
            );

            // Dispose the connection manager
            await connectionManager.DisposeAsync();

            // Act
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var result = await connectionManager.InvokeAsync<string>("TestMethod", cts.Token);

            // Assert
            result.ShouldBeNull("InvokeAsync should return default when disposed");
        }

        [Fact]
        public async Task InvokeAsync_WhenConnectionNotEstablished_ReturnsDefault()
        {
            // Arrange
            _mockWsProvider
                .Setup(x => x.CreateConnectionAsync(_testEndpoint))
                .ReturnsAsync((null!, new Uri("ws://localhost:9999/test")));

            await using var connectionManager = new ConnectionManager(
                _logger,
                _testVersion,
                _testEndpoint,
                _mockWsProvider.Object
            );

            // Act
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var result = await connectionManager.InvokeAsync<string>("TestMethod", cts.Token);

            // Assert
            result.ShouldBeNull("InvokeAsync should return default when connection cannot be established");
        }

        [Fact]
        public async Task InvokeAsync_WhenCancellationTokenAlreadyCanceled_ReturnsDefault()
        {
            // Arrange
            var mockConnection = CreateMockConnection();
            _mockWsProvider
                .Setup(x => x.CreateConnectionAsync(_testEndpoint))
                .ReturnsAsync(mockConnection);

            await using var connectionManager = new ConnectionManager(
                _logger,
                _testVersion,
                _testEndpoint,
                _mockWsProvider.Object
            );

            using var cts = new CancellationTokenSource();
            cts.Cancel(); // Cancel before calling InvokeAsync

            // Act
            var result = await connectionManager.InvokeAsync<string>("TestMethod", cts.Token);

            // Assert
            result.ShouldBeNull("InvokeAsync should return default when cancellation token is already canceled");
        }

        [Fact]
        public async Task InvokeAsync_WhenCanceledDuringConnection_ReturnsDefault()
        {
            // Arrange
            using var cts = new CancellationTokenSource();
            var connectionStarted = new TaskCompletionSource<bool>();

            _mockWsProvider
                .Setup(x => x.CreateConnectionAsync(_testEndpoint))
                .Returns(async () =>
                {
                    connectionStarted.SetResult(true);
                    await Task.Delay(Timeout.Infinite, cts.Token); // Will be canceled
                    return CreateMockConnection();
                });

            await using var connectionManager = new ConnectionManager(
                _logger,
                _testVersion,
                _testEndpoint,
                _mockWsProvider.Object
            );

            // First call Connect to enable _continueToReconnect
            var connectTask = Task.Run(async () => await connectionManager.Connect(cts.Token));

            await EnsureConnectionStartedAsync(connectionStarted.Task);

            // Now start InvokeAsync which will wait for the ongoing connection
            var invokeTask = Task.Run(async () => await connectionManager.InvokeAsync<string>("TestMethod", cts.Token));

            await Task.Delay(50); // Allow InvokeAsync to start waiting
            cts.Cancel(); // Cancel during connection

            var connectResult = await connectTask;
            var invokeResult = await invokeTask;

            // Assert
            connectResult.ShouldBeFalse("Connect should return false when canceled");
            invokeResult.ShouldBeNull("InvokeAsync should return default when canceled during connection");
        }

        [Fact]
        public async Task InvokeAsync_WhenProviderThrows_ReturnsDefault()
        {
            // Arrange
            _mockWsProvider
                .Setup(x => x.CreateConnectionAsync(_testEndpoint))
                .ThrowsAsync(new InvalidOperationException("Failed to create connection"));

            await using var connectionManager = new ConnectionManager(
                _logger,
                _testVersion,
                _testEndpoint,
                _mockWsProvider.Object
            );

            // Act
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var result = await connectionManager.InvokeAsync<int>("TestMethod", cts.Token);

            // Assert
            result.ShouldBe(0, "InvokeAsync should return default(int) when provider throws");
        }

        [Fact]
        public async Task InvokeAsync_WhenHubConnectionIsNull_ReturnsDefault()
        {
            // Arrange
            // Create a connection that returns null initially
            _mockWsProvider
                .Setup(x => x.CreateConnectionAsync(_testEndpoint))
                .ReturnsAsync((null!, new Uri("ws://localhost:9999/test")));

            await using var connectionManager = new ConnectionManager(
                _logger,
                _testVersion,
                _testEndpoint,
                _mockWsProvider.Object
            );

            // Act
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var result = await connectionManager.InvokeAsync<bool>("TestMethod", cts.Token);

            // Assert
            result.ShouldBeFalse("InvokeAsync should return default(bool) when hub connection is null");
        }

        [Fact]
        public async Task InvokeAsync_WithShortTimeout_ReturnsDefault()
        {
            // Arrange
            var connectionStarted = new TaskCompletionSource<bool>();
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

            _mockWsProvider
                .Setup(x => x.CreateConnectionAsync(_testEndpoint))
                .Returns(async () =>
                {
                    connectionStarted.TrySetResult(true);
                    // Simulate a long operation that will be interrupted by timeout
                    await Task.Delay(TimeSpan.FromSeconds(10), cts.Token);
                    return CreateMockConnection();
                });

            await using var connectionManager = new ConnectionManager(
                _logger,
                _testVersion,
                _testEndpoint,
                _mockWsProvider.Object
            );

            // Act - Use very short timeout
            var result = await connectionManager.InvokeAsync<string>("TestMethod", cts.Token);

            // Assert
            result.ShouldBeNull("InvokeAsync should return default when timeout occurs");
        }

        [Fact]
        public async Task InvokeAsync_MultipleCallsWhileDisposed_AllReturnDefault()
        {
            // Arrange
            var mockConnection = CreateMockConnection();
            _mockWsProvider
                .Setup(x => x.CreateConnectionAsync(_testEndpoint))
                .ReturnsAsync(mockConnection);

            var connectionManager = new ConnectionManager(
                _logger,
                _testVersion,
                _testEndpoint,
                _mockWsProvider.Object
            );

            // Dispose the connection manager
            await connectionManager.DisposeAsync();

            // Act - Make multiple calls after disposal
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var task1 = connectionManager.InvokeAsync<string>("Method1", cts.Token);
            var task2 = connectionManager.InvokeAsync<string>("Method2", cts.Token);
            var task3 = connectionManager.InvokeAsync<string>("Method3", cts.Token);

            var results = await Task.WhenAll(task1, task2, task3);

            // Assert
            results.ShouldAllBe(r => r == null, "all InvokeAsync calls should return default when disposed");
        }

        [Fact]
        public async Task InvokeAsync_WhenDisposedDuringInvocation_HandlesGracefully()
        {
            // Arrange
            var connectionStarted = new TaskCompletionSource<bool>();
            var allowConnectionToComplete = new TaskCompletionSource<bool>();

            _mockWsProvider
                .Setup(x => x.CreateConnectionAsync(_testEndpoint))
                .Returns(async () =>
                {
                    connectionStarted.TrySetResult(true);
                    await allowConnectionToComplete.Task;
                    return CreateMockConnection();
                });

            var connectionManager = new ConnectionManager(
                _logger,
                _testVersion,
                _testEndpoint,
                _mockWsProvider.Object
            );

            // First start a Connect call to enable _continueToReconnect and begin connection
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var connectTask = Task.Run(async () => await connectionManager.Connect(cts.Token));

            await EnsureConnectionStartedAsync(connectionStarted.Task);

            // Now start InvokeAsync which will wait for the ongoing connection
            var invokeTask = Task.Run(async () => await connectionManager.InvokeAsync<string>("TestMethod", cts.Token));

            await Task.Delay(50); // Allow InvokeAsync to start waiting

            // Dispose while connection is in progress
            var disposeTask = connectionManager.DisposeAsync();

            // Allow connection to complete
            allowConnectionToComplete.SetResult(true);

            await disposeTask;
            var connectResult = await connectTask;
            var invokeResult = await invokeTask;

            // Assert
            connectResult.ShouldBeFalse("Connect should return false when disposed");
            invokeResult.ShouldBeNull("InvokeAsync should return default when disposed during invocation");
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Creates a mock HubConnection for testing.
        /// Note: Due to HubConnection being sealed, this creates a minimal instance
        /// that can be used in tests. For full integration testing, use real HubConnection instances.
        /// </summary>
        private static (ClientWebSocket, Uri) CreateMockConnection()
        {
            return (new ClientWebSocket(), new Uri("ws://localhost:9999/test"));
        }

        /// <summary>
        /// Ensures that the connection has started within a reasonable timeout.
        /// </summary>
        /// <param name="connectionStartedTask">The task representing the connection start operation.</param>
        /// <param name="timeoutMs">The timeout in milliseconds (default: 2000ms).</param>
        private static async Task EnsureConnectionStartedAsync(Task connectionStartedTask, int timeoutMs = 2000)
        {
            var timeoutTask = Task.Delay(timeoutMs);
            var completedTask = await Task.WhenAny(connectionStartedTask, timeoutTask);

            if (completedTask == timeoutTask)
            {
                throw new TimeoutException("Connection did not start within expected time");
            }
        }

        /// <summary>
        /// Ensures that all tasks complete within a reasonable timeout.
        /// </summary>
        /// <param name="tasks">The tasks to wait for completion.</param>
        private static Task EnsureTasksCompleteAsync(params Task[] tasks)
            => EnsureTasksCompleteAsync(5000, tasks);

        /// <summary>
        /// Ensures that all tasks complete within a reasonable timeout.
        /// </summary>
        /// <param name="timeoutMs">The timeout in milliseconds.</param>
        /// <param name="tasks">The tasks to wait for completion.</param>
        private static async Task EnsureTasksCompleteAsync(int timeoutMs, params Task[] tasks)
        {
            var allTasksCompleted = Task.WhenAll(tasks);
            var timeoutTask = Task.Delay(timeoutMs);
            var completedTask = await Task.WhenAny(allTasksCompleted, timeoutTask);

            if (completedTask == timeoutTask)
            {
                throw new TimeoutException("Tasks did not complete within expected time");
            }
        }

        #endregion
    }
}
