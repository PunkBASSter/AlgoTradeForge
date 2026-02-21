using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Tests.TestUtilities;
using AlgoTradeForge.Domain.Trading;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Engine;

public class DebugProbeTests
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
    public void Run_WithoutProbe_DoesNotThrow()
    {
        var sub = new DataSubscription(TestAssets.Aapl, OneMinute);
        var strategy = Substitute.For<IInt64BarStrategy>();
        strategy.DataSubscriptions.Returns(new List<DataSubscription> { sub });

        var bars = TestBars.CreateSeries(Start, OneMinute, 3);

        var result = CreateEngine().Run([bars], strategy, CreateOptions());

        Assert.Equal(3, result.TotalBarsProcessed);
    }

    [Fact]
    public void NullDebugProbe_IsNotActive()
    {
        Assert.False(NullDebugProbe.Instance.IsActive);
    }

    [Fact]
    public void Run_WithNullProbe_NeverCallsProbeMethods()
    {
        var probe = Substitute.For<IDebugProbe>();
        probe.IsActive.Returns(false);

        var sub = new DataSubscription(TestAssets.Aapl, OneMinute);
        var strategy = Substitute.For<IInt64BarStrategy>();
        strategy.DataSubscriptions.Returns(new List<DataSubscription> { sub });

        var bars = TestBars.CreateSeries(Start, OneMinute, 5);

        CreateEngine().Run([bars], strategy, CreateOptions(), probe: probe);

        probe.DidNotReceive().OnRunStart();
        probe.DidNotReceive().OnBarProcessed(Arg.Any<DebugSnapshot>());
        probe.DidNotReceive().OnRunEnd();
    }

    [Fact]
    public void Run_WithActiveProbe_CallsLifecycleInOrder()
    {
        var calls = new List<string>();
        var probe = new RecordingDebugProbe(calls);

        var sub = new DataSubscription(TestAssets.Aapl, OneMinute);
        var strategy = Substitute.For<IInt64BarStrategy>();
        strategy.DataSubscriptions.Returns(new List<DataSubscription> { sub });

        var bars = TestBars.CreateSeries(Start, OneMinute, 3);

        CreateEngine().Run([bars], strategy, CreateOptions(), probe: probe);

        Assert.Equal("OnRunStart", calls[0]);
        Assert.Equal("OnBarProcessed:1", calls[1]);
        Assert.Equal("OnBarProcessed:2", calls[2]);
        Assert.Equal("OnBarProcessed:3", calls[3]);
        Assert.Equal("OnRunEnd", calls[4]);
        Assert.Equal(5, calls.Count);
    }

    [Fact]
    public void Run_WithActiveProbe_SequenceNumbersAreMonotonic()
    {
        var snapshots = new List<DebugSnapshot>();
        var probe = new RecordingDebugProbe(snapshots: snapshots);

        var sub = new DataSubscription(TestAssets.Aapl, OneMinute);
        var strategy = Substitute.For<IInt64BarStrategy>();
        strategy.DataSubscriptions.Returns(new List<DataSubscription> { sub });

        var bars = TestBars.CreateSeries(Start, OneMinute, 5);

        CreateEngine().Run([bars], strategy, CreateOptions(), probe: probe);

        Assert.Equal(5, snapshots.Count);
        for (var i = 0; i < snapshots.Count; i++)
            Assert.Equal(i + 1, snapshots[i].SequenceNumber);
    }

    [Fact]
    public void Run_WithActiveProbe_SnapshotContainsCorrectTimestamp()
    {
        var snapshots = new List<DebugSnapshot>();
        var probe = new RecordingDebugProbe(snapshots: snapshots);

        var sub = new DataSubscription(TestAssets.Aapl, OneMinute);
        var strategy = Substitute.For<IInt64BarStrategy>();
        strategy.DataSubscriptions.Returns(new List<DataSubscription> { sub });

        var bars = TestBars.CreateSeries(Start, OneMinute, 2);

        CreateEngine().Run([bars], strategy, CreateOptions(), probe: probe);

        Assert.Equal(Start.ToUnixTimeMilliseconds(), snapshots[0].TimestampMs);
        Assert.Equal(Start.AddMinutes(1).ToUnixTimeMilliseconds(), snapshots[1].TimestampMs);
    }

    [Fact]
    public void Run_WithActiveProbe_ReportsIsExportable()
    {
        var snapshots = new List<DebugSnapshot>();
        var probe = new RecordingDebugProbe(snapshots: snapshots);

        var exportable = new DataSubscription(TestAssets.Aapl, OneMinute, IsExportable: true);
        var strategy = Substitute.For<IInt64BarStrategy>();
        strategy.DataSubscriptions.Returns(new List<DataSubscription> { exportable });

        var bars = TestBars.CreateSeries(Start, OneMinute, 1);

        CreateEngine().Run([bars], strategy, CreateOptions(), probe: probe);

        Assert.Single(snapshots);
        Assert.True(snapshots[0].IsExportableSubscription);
    }

    [Fact]
    public void Run_WithActiveProbe_ReportsFillCount()
    {
        var snapshots = new List<DebugSnapshot>();
        var probe = new RecordingDebugProbe(snapshots: snapshots);

        var sub = new DataSubscription(TestAssets.Aapl, OneMinute);
        var submitted = false;

        var strategy = new SimpleOrderStrategy(sub, (_, _, orders) =>
        {
            if (submitted) return;
            submitted = true;
            orders.Submit(new Order
            {
                Id = 0,
                Asset = TestAssets.Aapl,
                Side = OrderSide.Buy,
                Type = OrderType.Market,
                Quantity = 1m
            });
        });

        // Bar 0: submit order in OnBarComplete → fills on bar 1
        var bars = TestBars.CreateSeries(Start, OneMinute, 2, startPrice: 1000);

        CreateEngine().Run([bars], strategy, CreateOptions(), probe: probe);

        Assert.Equal(2, snapshots.Count);
        Assert.Equal(0, snapshots[0].FillsThisBar); // bar 0: order submitted, not yet filled
        Assert.Equal(1, snapshots[1].FillsThisBar); // bar 1: order fills
    }

    [Fact]
    public void Run_WithActiveProbe_MultiSubscription_ReportsCorrectIndex()
    {
        var snapshots = new List<DebugSnapshot>();
        var probe = new RecordingDebugProbe(snapshots: snapshots);

        var sub0 = new DataSubscription(TestAssets.Aapl, OneMinute);
        var sub1 = new DataSubscription(TestAssets.BtcUsdt, OneMinute);
        var strategy = Substitute.For<IInt64BarStrategy>();
        strategy.DataSubscriptions.Returns(new List<DataSubscription> { sub0, sub1 });

        var bars0 = TestBars.CreateSeries(Start, OneMinute, 1, startPrice: 1000);
        var bars1 = TestBars.CreateSeries(Start, OneMinute, 1, startPrice: 5000);

        CreateEngine().Run([bars0, bars1], strategy, CreateOptions(), probe: probe);

        // Both bars have the same timestamp, delivered in subscription order
        Assert.Equal(2, snapshots.Count);
        Assert.Equal(0, snapshots[0].SubscriptionIndex);
        Assert.Equal(1, snapshots[1].SubscriptionIndex);
    }

    [Fact]
    public void Run_WithActiveProbe_CallsOnRunEnd_WhenStrategyThrows()
    {
        var calls = new List<string>();
        var probe = new RecordingDebugProbe(calls);

        var sub = new DataSubscription(TestAssets.Aapl, OneMinute);
        var strategy = Substitute.For<IInt64BarStrategy>();
        strategy.DataSubscriptions.Returns(new List<DataSubscription> { sub });
        strategy.When(s => s.OnBarComplete(Arg.Any<Int64Bar>(), Arg.Any<DataSubscription>(), Arg.Any<IOrderContext>()))
            .Do(_ => throw new InvalidOperationException("Strategy error"));

        var bars = TestBars.CreateSeries(Start, OneMinute, 1);

        Assert.Throws<InvalidOperationException>(() =>
            CreateEngine().Run([bars], strategy, CreateOptions(), probe: probe));

        Assert.Contains("OnRunEnd", calls);
    }

    [Fact]
    public void Run_WithActiveProbe_CallsOnRunEnd_OnCancellation()
    {
        var calls = new List<string>();
        var probe = new RecordingDebugProbe(calls);

        var sub = new DataSubscription(TestAssets.Aapl, OneMinute);
        using var cts = new CancellationTokenSource();
        var strategy = Substitute.For<IInt64BarStrategy>();
        strategy.DataSubscriptions.Returns(new List<DataSubscription> { sub });
        strategy.When(s => s.OnBarComplete(Arg.Any<Int64Bar>(), Arg.Any<DataSubscription>(), Arg.Any<IOrderContext>()))
            .Do(_ => cts.Cancel());

        var bars = TestBars.CreateSeries(Start, OneMinute, 2);

        Assert.Throws<OperationCanceledException>(() =>
            CreateEngine().Run([bars], strategy, CreateOptions(), cts.Token, probe: probe));

        Assert.Contains("OnRunEnd", calls);
    }

    [Fact]
    public void Run_EmptySeries_ProbeReceivesStartAndEndOnly()
    {
        var calls = new List<string>();
        var probe = new RecordingDebugProbe(calls);

        var sub = new DataSubscription(TestAssets.Aapl, OneMinute);
        var strategy = Substitute.For<IInt64BarStrategy>();
        strategy.DataSubscriptions.Returns(new List<DataSubscription> { sub });

        var bars = new TimeSeries<Int64Bar>();

        CreateEngine().Run([bars], strategy, CreateOptions(), probe: probe);

        Assert.Equal(["OnRunStart", "OnRunEnd"], calls);
    }

    private sealed class RecordingDebugProbe(
        List<string>? calls = null,
        List<DebugSnapshot>? snapshots = null) : IDebugProbe
    {
        public bool IsActive => true;

        public void OnRunStart() => calls?.Add("OnRunStart");

        public void OnBarProcessed(DebugSnapshot snapshot)
        {
            calls?.Add($"OnBarProcessed:{snapshot.SequenceNumber}");
            snapshots?.Add(snapshot);
        }

        public void OnRunEnd() => calls?.Add("OnRunEnd");
    }

    private sealed class SimpleOrderStrategy(
        DataSubscription subscription,
        Action<Int64Bar, DataSubscription, IOrderContext> onBarComplete) : IInt64BarStrategy
    {
        public string Version => "1.0.0";
        public IList<DataSubscription> DataSubscriptions { get; } = [subscription];

        public void OnInit() { }
        public void OnTrade(Fill fill, Order order) { }

        public void OnBarComplete(Int64Bar bar, DataSubscription subscription, IOrderContext orders)
            => onBarComplete(bar, subscription, orders);
    }
}
