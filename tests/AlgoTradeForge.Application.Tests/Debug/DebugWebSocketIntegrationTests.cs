using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using AlgoTradeForge.Application.Debug;
using AlgoTradeForge.Application.Events;
using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.Events;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Application.Tests.TestUtilities;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.Application.Tests.Debug;

public sealed class DebugWebSocketIntegrationTests
{
    private static readonly DateTimeOffset Start = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan OneMinute = TimeSpan.FromMinutes(1);

    private static BacktestOptions CreateOptions(long initialCash = 100_000L) =>
        new()
        {
            InitialCash = initialCash,
            Asset = TestAssets.Aapl,
            StartTime = DateTimeOffset.MinValue,
            EndTime = DateTimeOffset.MaxValue,
        };

    private static BacktestEngine CreateEngine() =>
        new(new BarMatcher(), new BasicRiskEvaluator());

    /// <summary>
    /// Full integration test:
    /// connect WebSocket → start session → send next_bar → receive events + snapshot
    /// → send continue → receive remaining events → session completes
    /// </summary>
    [Fact]
    public async Task WebSocket_FullDebugFlow_EventsAndSnapshotsDelivered()
    {
        // Arrange
        using var probe = new GatingDebugProbe();
        await using var wsSink = new WebSocketSink();
        var (serverWs, clientWs) = DuplexStreamPair.CreateLinkedWebSockets();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        wsSink.Attach(serverWs, cts.Token);

        var sub = new DataSubscription(TestAssets.Aapl, OneMinute, IsExportable: true);
        var strategy = Substitute.For<IInt64BarStrategy>();
        strategy.DataSubscriptions.Returns(new List<DataSubscription> { sub });

        var bars = TestBars.CreateSeries(Start, OneMinute, 5);
        var eventBus = new EventBus(ExportMode.Backtest, [wsSink]);

        // Start background reader that collects all WebSocket messages
        var received = Channel.CreateUnbounded<string>();
        var readerTask = ReadAllMessagesAsync(clientWs, received.Writer, cts.Token);

        var engineTask = Task.Factory.StartNew(
            () => CreateEngine().Run([bars], strategy, CreateOptions(), probe: probe, bus: eventBus),
            TaskCreationOptions.LongRunning);

        // Act 1: Step to first bar
        var snap1 = await probe.SendCommandAsync(new DebugCommand.NextBar(), cts.Token);
        Assert.Equal(1, snap1.SequenceNumber);

        // Poll until events arrive from the send loop
        var firstBarEvents = await DrainUntilAsync(received.Reader,
            msgs => msgs.Any(e => e.Contains("\"_t\":\"bar\"")), cts.Token);
        Assert.True(firstBarEvents.Count > 0, "Should receive at least run.start and bar events");
        Assert.Contains(firstBarEvents, e => e.Contains("\"_t\":\"run.start\""));
        Assert.Contains(firstBarEvents, e => e.Contains("\"_t\":\"bar\""));

        // Act 2: Step another bar
        var snap2 = await probe.SendCommandAsync(new DebugCommand.NextBar(), cts.Token);
        Assert.Equal(2, snap2.SequenceNumber);

        var secondBarEvents = await DrainUntilAsync(received.Reader,
            msgs => msgs.Any(e => e.Contains("\"_t\":\"bar\"")), cts.Token);
        Assert.Contains(secondBarEvents, e => e.Contains("\"_t\":\"bar\""));

        // Act 3: Continue — engine runs to completion
        await probe.SendCommandAsync(new DebugCommand.Continue(), cts.Token);

        // Wait for engine to complete
        var result = await engineTask.WaitAsync(cts.Token);
        Assert.Equal(5, result.TotalBarsProcessed);

        // Poll until run.end event arrives
        var remainingEvents = await DrainUntilAsync(received.Reader,
            msgs => msgs.Any(e => e.Contains("\"_t\":\"run.end\"")), cts.Token);

        // Verify we got bar events for remaining bars and run.end event
        var allEvents = firstBarEvents.Concat(secondBarEvents).Concat(remainingEvents).ToList();
        Assert.Contains(allEvents, e => e.Contains("\"_t\":\"run.end\""));

        // Count bar events — should have 5 total (one per bar, all exportable)
        var barEventCount = allEvents.Count(e => e.Contains("\"_t\":\"bar\""));
        Assert.Equal(5, barEventCount);

        // Cleanup: cancel reader
        await cts.CancelAsync();
        try { await readerTask; } catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Two concurrent sessions on separate WebSocket connections don't interfere.
    /// </summary>
    [Fact]
    public async Task TwoConcurrentSessions_DoNotInterfere()
    {
        // Arrange session 1: 3 bars
        using var probe1 = new GatingDebugProbe();
        await using var sink1 = new WebSocketSink();
        var (server1, client1) = DuplexStreamPair.CreateLinkedWebSockets();

        // Arrange session 2: 4 bars
        using var probe2 = new GatingDebugProbe();
        await using var sink2 = new WebSocketSink();
        var (server2, client2) = DuplexStreamPair.CreateLinkedWebSockets();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        sink1.Attach(server1, cts.Token);
        sink2.Attach(server2, cts.Token);

        var sub = new DataSubscription(TestAssets.Aapl, OneMinute, IsExportable: true);
        var strategy1 = Substitute.For<IInt64BarStrategy>();
        strategy1.DataSubscriptions.Returns(new List<DataSubscription> { sub });
        var strategy2 = Substitute.For<IInt64BarStrategy>();
        strategy2.DataSubscriptions.Returns(new List<DataSubscription> { sub });

        var bars1 = TestBars.CreateSeries(Start, OneMinute, 3);
        var bars2 = TestBars.CreateSeries(Start, OneMinute, 4);

        var bus1 = new EventBus(ExportMode.Backtest, [sink1]);
        var bus2 = new EventBus(ExportMode.Backtest, [sink2]);

        // Start background readers
        var received1 = Channel.CreateUnbounded<string>();
        var received2 = Channel.CreateUnbounded<string>();
        var reader1Task = ReadAllMessagesAsync(client1, received1.Writer, cts.Token);
        var reader2Task = ReadAllMessagesAsync(client2, received2.Writer, cts.Token);

        var engine1Task = Task.Factory.StartNew(
            () => CreateEngine().Run([bars1], strategy1, CreateOptions(), probe: probe1, bus: bus1),
            TaskCreationOptions.LongRunning);
        var engine2Task = Task.Factory.StartNew(
            () => CreateEngine().Run([bars2], strategy2, CreateOptions(), probe: probe2, bus: bus2),
            TaskCreationOptions.LongRunning);

        // Act: Step session 1 once, session 2 once
        var snap1 = await probe1.SendCommandAsync(new DebugCommand.NextBar(), cts.Token);
        Assert.Equal(1, snap1.SequenceNumber);

        var snap2 = await probe2.SendCommandAsync(new DebugCommand.NextBar(), cts.Token);
        Assert.Equal(1, snap2.SequenceNumber);

        // Continue both sessions
        await probe1.SendCommandAsync(new DebugCommand.Continue(), cts.Token);
        await probe2.SendCommandAsync(new DebugCommand.Continue(), cts.Token);

        var result1 = await engine1Task.WaitAsync(cts.Token);
        var result2 = await engine2Task.WaitAsync(cts.Token);

        // Assert: each session processed the correct number of bars
        Assert.Equal(3, result1.TotalBarsProcessed);
        Assert.Equal(4, result2.TotalBarsProcessed);

        // Poll until events arrive from each session's send loop
        var events1 = await DrainUntilAsync(received1.Reader,
            msgs => msgs.Any(e => e.Contains("\"_t\":\"run.end\"")), cts.Token);
        var events2 = await DrainUntilAsync(received2.Reader,
            msgs => msgs.Any(e => e.Contains("\"_t\":\"run.end\"")), cts.Token);

        // Each session should have its own set of bar events
        var barCount1 = events1.Count(e => e.Contains("\"_t\":\"bar\""));
        var barCount2 = events2.Count(e => e.Contains("\"_t\":\"bar\""));
        Assert.Equal(3, barCount1);
        Assert.Equal(4, barCount2);

        // Cleanup
        await cts.CancelAsync();
        try { await reader1Task; } catch (OperationCanceledException) { }
        try { await reader2Task; } catch (OperationCanceledException) { }
    }

    private static async Task ReadAllMessagesAsync(
        WebSocket ws, ChannelWriter<string> writer, CancellationToken ct)
    {
        var buffer = new byte[8192];
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(buffer.AsMemory(), ct);
                if (result.MessageType == WebSocketMessageType.Text)
                    writer.TryWrite(Encoding.UTF8.GetString(buffer, 0, result.Count));
                else if (result.MessageType == WebSocketMessageType.Close)
                    break;
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        writer.TryComplete();
    }

    private static List<string> DrainAvailable(ChannelReader<string> reader)
    {
        var messages = new List<string>();
        while (reader.TryRead(out var msg))
            messages.Add(msg);
        return messages;
    }

    /// <summary>
    /// Polls the channel reader until <paramref name="predicate"/> is satisfied or cancellation.
    /// Replaces fixed <c>Task.Delay</c> waits with deterministic condition checks.
    /// </summary>
    private static async Task<List<string>> DrainUntilAsync(
        ChannelReader<string> reader,
        Func<List<string>, bool> predicate,
        CancellationToken ct)
    {
        var messages = new List<string>();
        while (!ct.IsCancellationRequested)
        {
            // Drain whatever is available right now
            while (reader.TryRead(out var msg))
                messages.Add(msg);

            if (predicate(messages))
                break;

            // Wait for more data (with timeout via ct)
            if (!await reader.WaitToReadAsync(ct).ConfigureAwait(false))
                break; // channel completed
        }

        return messages;
    }
}
