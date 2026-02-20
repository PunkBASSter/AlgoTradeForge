using AlgoTradeForge.Application.Debug;
using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Tests.TestUtilities;
using AlgoTradeForge.Domain.Trading;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Engine;

public class GatingDebugProbeTests
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

    [Fact]
    public async Task NextBar_StepsOneBarAtATime()
    {
        using var probe = new GatingDebugProbe();
        var sub = new DataSubscription(TestAssets.Aapl, OneMinute);
        var strategy = Substitute.For<IInt64BarStrategy>();
        strategy.DataSubscriptions.Returns(new List<DataSubscription> { sub });

        var bars = TestBars.CreateSeries(Start, OneMinute, 5);

        var engineTask = Task.Factory.StartNew(
            () => CreateEngine().Run([bars], strategy, CreateOptions(), probe: probe),
            TaskCreationOptions.LongRunning);

        // Step through each bar
        var snap1 = await probe.SendCommandAsync(new DebugCommand.NextBar());
        Assert.Equal(1, snap1.SequenceNumber);

        var snap2 = await probe.SendCommandAsync(new DebugCommand.NextBar());
        Assert.Equal(2, snap2.SequenceNumber);

        var snap3 = await probe.SendCommandAsync(new DebugCommand.NextBar());
        Assert.Equal(3, snap3.SequenceNumber);

        // Continue to finish
        await probe.SendCommandAsync(new DebugCommand.Continue());
        var result = await engineTask;

        Assert.Equal(5, result.TotalBarsProcessed);
    }

    [Fact]
    public async Task Continue_RunsToCompletion()
    {
        using var probe = new GatingDebugProbe();
        var sub = new DataSubscription(TestAssets.Aapl, OneMinute);
        var strategy = Substitute.For<IInt64BarStrategy>();
        strategy.DataSubscriptions.Returns(new List<DataSubscription> { sub });

        var bars = TestBars.CreateSeries(Start, OneMinute, 10);

        var engineTask = Task.Factory.StartNew(
            () => CreateEngine().Run([bars], strategy, CreateOptions(), probe: probe),
            TaskCreationOptions.LongRunning);

        // Send continue — engine runs to completion
        await probe.SendCommandAsync(new DebugCommand.Continue());
        var result = await engineTask;

        Assert.Equal(10, result.TotalBarsProcessed);
    }

    [Fact]
    public async Task Next_SkipsNonExportableSubscriptions()
    {
        using var probe = new GatingDebugProbe();
        var nonExportable = new DataSubscription(TestAssets.Aapl, OneMinute, IsExportable: false);
        var exportable = new DataSubscription(TestAssets.BtcUsdt, OneMinute, IsExportable: true);
        var strategy = Substitute.For<IInt64BarStrategy>();
        strategy.DataSubscriptions.Returns(new List<DataSubscription> { nonExportable, exportable });

        // Both have 1 bar at the same timestamp — 2 bars total per timestamp
        var bars0 = TestBars.CreateSeries(Start, OneMinute, 2, startPrice: 1000);
        var bars1 = TestBars.CreateSeries(Start, OneMinute, 2, startPrice: 5000);

        var engineTask = Task.Factory.StartNew(
            () => CreateEngine().Run([bars0, bars1], strategy, CreateOptions(), probe: probe),
            TaskCreationOptions.LongRunning);

        // Next should skip non-exportable (sub 0) and land on exportable (sub 1)
        var snap = await probe.SendCommandAsync(new DebugCommand.Next());
        Assert.True(snap.IsExportableSubscription);
        Assert.Equal(1, snap.SubscriptionIndex);

        await probe.SendCommandAsync(new DebugCommand.Continue());
        await engineTask;
    }

    [Fact]
    public async Task NextTrade_SkipsBarsWithNoFills()
    {
        using var probe = new GatingDebugProbe();
        var sub = new DataSubscription(TestAssets.Aapl, OneMinute);

        var strategy = new OrderOnBar2Strategy(sub);

        // 5 bars: order submitted in OnBarComplete of bar 1, fills on bar 2
        var bars = TestBars.CreateSeries(Start, OneMinute, 5, startPrice: 1000);

        var engineTask = Task.Factory.StartNew(
            () => CreateEngine().Run([bars], strategy, CreateOptions(), probe: probe),
            TaskCreationOptions.LongRunning);

        // NextTrade should skip bars without fills and land on bar 2 (where the fill happens)
        var snap = await probe.SendCommandAsync(new DebugCommand.NextTrade());
        Assert.True(snap.FillsThisBar > 0);
        Assert.Equal(2, snap.SequenceNumber); // bar index 1 (0-based) = sequence 2

        await probe.SendCommandAsync(new DebugCommand.Continue());
        await engineTask;
    }

    [Fact]
    public async Task RunToSequence_RunsToTargetAndPauses()
    {
        using var probe = new GatingDebugProbe();
        var sub = new DataSubscription(TestAssets.Aapl, OneMinute);
        var strategy = Substitute.For<IInt64BarStrategy>();
        strategy.DataSubscriptions.Returns(new List<DataSubscription> { sub });

        var bars = TestBars.CreateSeries(Start, OneMinute, 10);

        var engineTask = Task.Factory.StartNew(
            () => CreateEngine().Run([bars], strategy, CreateOptions(), probe: probe),
            TaskCreationOptions.LongRunning);

        var snap = await probe.SendCommandAsync(new DebugCommand.RunToSequence(5));
        Assert.Equal(5, snap.SequenceNumber);

        Assert.True(probe.IsRunning);

        await probe.SendCommandAsync(new DebugCommand.Continue());
        await engineTask;
    }

    [Fact]
    public async Task RunToTimestamp_RunsToTargetAndPauses()
    {
        using var probe = new GatingDebugProbe();
        var sub = new DataSubscription(TestAssets.Aapl, OneMinute);
        var strategy = Substitute.For<IInt64BarStrategy>();
        strategy.DataSubscriptions.Returns(new List<DataSubscription> { sub });

        var bars = TestBars.CreateSeries(Start, OneMinute, 10);

        var engineTask = Task.Factory.StartNew(
            () => CreateEngine().Run([bars], strategy, CreateOptions(), probe: probe),
            TaskCreationOptions.LongRunning);

        var targetMs = Start.AddMinutes(4).ToUnixTimeMilliseconds();
        var snap = await probe.SendCommandAsync(new DebugCommand.RunToTimestamp(targetMs));
        Assert.Equal(targetMs, snap.TimestampMs);

        await probe.SendCommandAsync(new DebugCommand.Continue());
        await engineTask;
    }

    [Fact]
    public async Task Pause_AfterContinue_StopsAtNextBar()
    {
        using var probe = new GatingDebugProbe();
        var sub = new DataSubscription(TestAssets.Aapl, OneMinute);

        // Use a strategy that signals when it's mid-run, so we can reliably pause
        var midRunSignal = new ManualResetEventSlim(false);
        var pauseGate = new ManualResetEventSlim(false);
        var barCount = 0;

        var strategy = new CallbackStrategy(sub, onBarComplete: (_, _, _) =>
        {
            var n = Interlocked.Increment(ref barCount);
            if (n == 5)
            {
                midRunSignal.Set();    // signal: engine is running
                pauseGate.Wait();      // hold engine here until test sends Pause
            }
        });

        var bars = TestBars.CreateSeries(Start, OneMinute, 20);

        var engineTask = Task.Factory.StartNew(
            () => CreateEngine().Run([bars], strategy, CreateOptions(), probe: probe),
            TaskCreationOptions.LongRunning);

        // Start running
        _ = probe.SendCommandAsync(new DebugCommand.Continue());

        // Wait until engine is mid-run (at bar 5)
        midRunSignal.Wait();

        // Send Pause while engine is blocked at bar 5's OnBarComplete
        var pauseTask = probe.SendCommandAsync(new DebugCommand.Pause());

        // Release the engine — it will process bar 5's probe call and should pause
        pauseGate.Set();

        var snap = await pauseTask;
        Assert.True(snap.SequenceNumber >= 5);
        Assert.True(snap.SequenceNumber <= 20);
        Assert.True(probe.IsRunning);

        await probe.SendCommandAsync(new DebugCommand.Continue());
        var result = await engineTask;

        Assert.Equal(20, result.TotalBarsProcessed);
    }

    [Fact]
    public async Task Dispose_UnblocksEngineThread()
    {
        var probe = new GatingDebugProbe();
        var sub = new DataSubscription(TestAssets.Aapl, OneMinute);
        var strategy = Substitute.For<IInt64BarStrategy>();
        strategy.DataSubscriptions.Returns(new List<DataSubscription> { sub });

        var bars = TestBars.CreateSeries(Start, OneMinute, 100);

        var engineTask = Task.Factory.StartNew(
            () => CreateEngine().Run([bars], strategy, CreateOptions(), probe: probe),
            TaskCreationOptions.LongRunning);

        // Dispose without sending any command — should unblock the engine
        probe.Dispose();

        // Engine should complete (running freely after dispose unblocks the gate)
        var result = await engineTask;
        Assert.True(result.TotalBarsProcessed > 0);
    }

    [Fact]
    public async Task SendCommand_AfterRunCompletes_ReturnsLastSnapshot()
    {
        using var probe = new GatingDebugProbe();
        var sub = new DataSubscription(TestAssets.Aapl, OneMinute);
        var strategy = Substitute.For<IInt64BarStrategy>();
        strategy.DataSubscriptions.Returns(new List<DataSubscription> { sub });

        var bars = TestBars.CreateSeries(Start, OneMinute, 3);

        var engineTask = Task.Factory.StartNew(
            () => CreateEngine().Run([bars], strategy, CreateOptions(), probe: probe),
            TaskCreationOptions.LongRunning);

        await probe.SendCommandAsync(new DebugCommand.Continue());
        await engineTask;

        Assert.False(probe.IsRunning);

        // Sending another command after completion should not block
        var snap = await probe.SendCommandAsync(new DebugCommand.NextBar());
        Assert.Equal(3, snap.SequenceNumber);
        Assert.Equal(Start.AddMinutes(2).ToUnixTimeMilliseconds(), snap.TimestampMs);
    }

    [Fact]
    public async Task ConcurrentSendCommand_ThrowsInvalidOperation()
    {
        using var probe = new GatingDebugProbe();
        var sub = new DataSubscription(TestAssets.Aapl, OneMinute);

        // Use a strategy that blocks the engine at bar 3 so the first command stays pending
        var gate = new ManualResetEventSlim(false);
        var reachedGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var barCount = 0;

        var strategy = new CallbackStrategy(sub, onBarComplete: (_, _, _) =>
        {
            if (Interlocked.Increment(ref barCount) == 3)
            {
                reachedGate.TrySetResult();
                gate.Wait(); // block engine at bar 3
            }
        });

        var bars = TestBars.CreateSeries(Start, OneMinute, 10);

        var engineTask = Task.Factory.StartNew(
            () => CreateEngine().Run([bars], strategy, CreateOptions(), probe: probe),
            TaskCreationOptions.LongRunning);

        // Send NextTrade — engine processes bars looking for a fill (none will happen)
        // so the command stays pending while the engine runs
        var firstCmd = probe.SendCommandAsync(new DebugCommand.NextTrade());

        // Wait until the engine is running and blocked at bar 3
        await reachedGate.Task;

        // Second concurrent command should throw
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => probe.SendCommandAsync(new DebugCommand.NextBar()));

        // Cleanup: release gate, then continue
        gate.Set();
        // The first command is waiting for a fill — won't get one, so continue to end
        probe.Dispose();
        try { await firstCmd; } catch { }
        try { await engineTask; } catch { }
    }

    [Fact]
    public async Task SendCommand_WithCancelledToken_ThrowsTaskCanceled()
    {
        using var probe = new GatingDebugProbe();
        var sub = new DataSubscription(TestAssets.Aapl, OneMinute);

        // Use a strategy that blocks so the probe stays paused
        var gate = new ManualResetEventSlim(false);
        var reachedGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var barCount = 0;

        var strategy = new CallbackStrategy(sub, onBarComplete: (_, _, _) =>
        {
            if (Interlocked.Increment(ref barCount) == 2)
            {
                reachedGate.TrySetResult();
                gate.Wait();
            }
        });

        var bars = TestBars.CreateSeries(Start, OneMinute, 10);

        var engineTask = Task.Factory.StartNew(
            () => CreateEngine().Run([bars], strategy, CreateOptions(), probe: probe),
            TaskCreationOptions.LongRunning);

        // Step to bar 1 first
        var snap1 = await probe.SendCommandAsync(new DebugCommand.NextBar());
        Assert.Equal(1, snap1.SequenceNumber);

        // Now send NextTrade with a cancellation token
        using var cts = new CancellationTokenSource();
        var nextTradeTask = probe.SendCommandAsync(new DebugCommand.NextTrade(), cts.Token);

        // Wait until the engine is running but blocked at bar 2
        await reachedGate.Task;

        // Cancel the token while the command is pending
        cts.Cancel();

        await Assert.ThrowsAsync<TaskCanceledException>(() => nextTradeTask);

        // Cleanup
        gate.Set();
        probe.Dispose();
        try { await engineTask; } catch { }
    }

    [Fact]
    public async Task RunToSequence_AlreadyPastTarget_BreaksImmediately()
    {
        using var probe = new GatingDebugProbe();
        var sub = new DataSubscription(TestAssets.Aapl, OneMinute);
        var strategy = Substitute.For<IInt64BarStrategy>();
        strategy.DataSubscriptions.Returns(new List<DataSubscription> { sub });

        var bars = TestBars.CreateSeries(Start, OneMinute, 10);

        var engineTask = Task.Factory.StartNew(
            () => CreateEngine().Run([bars], strategy, CreateOptions(), probe: probe),
            TaskCreationOptions.LongRunning);

        // Step to bar 5
        var snap = await probe.SendCommandAsync(new DebugCommand.RunToSequence(5));
        Assert.Equal(5, snap.SequenceNumber);

        // Now run to sequence 3 — already past, should break on very next bar (6)
        var snap2 = await probe.SendCommandAsync(new DebugCommand.RunToSequence(3));
        Assert.Equal(6, snap2.SequenceNumber);
        Assert.True(snap2.SequenceNumber >= 3); // condition satisfied immediately

        await probe.SendCommandAsync(new DebugCommand.Continue());
        await engineTask;
    }

    private sealed class OrderOnBar2Strategy(DataSubscription subscription) : IInt64BarStrategy
    {
        private bool _submitted;
        public IList<DataSubscription> DataSubscriptions { get; } = [subscription];
        public void OnInit() { }
        public void OnTrade(Fill fill, Order order) { }

        public void OnBarComplete(Int64Bar bar, DataSubscription sub, IOrderContext orders)
        {
            if (_submitted) return;
            _submitted = true;
            orders.Submit(new Order
            {
                Id = 0,
                Asset = TestAssets.Aapl,
                Side = OrderSide.Buy,
                Type = OrderType.Market,
                Quantity = 1m
            });
        }
    }

    private sealed class CallbackStrategy(
        DataSubscription subscription,
        Action<Int64Bar, DataSubscription, IOrderContext>? onBarComplete = null) : IInt64BarStrategy
    {
        public IList<DataSubscription> DataSubscriptions { get; } = [subscription];
        public void OnInit() { }
        public void OnTrade(Fill fill, Order order) { }

        public void OnBarComplete(Int64Bar bar, DataSubscription sub, IOrderContext orders)
            => onBarComplete?.Invoke(bar, sub, orders);
    }
}
