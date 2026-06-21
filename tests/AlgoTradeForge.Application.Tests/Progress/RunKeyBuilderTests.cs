using AlgoTradeForge.Application.Backtests;
using AlgoTradeForge.LiveHost.Application.Live;
using AlgoTradeForge.Application.Progress;
using Xunit;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.Subscriptions;

namespace AlgoTradeForge.Application.Tests.Progress;

public sealed class RunKeyBuilderTests
{
    [Fact]
    public void Build_Backtest_Identical_Params_Produces_Same_Key()
    {
        var cmd = MakeBacktestCommand();
        var key1 = RunKeyBuilder.Build(cmd);
        var key2 = RunKeyBuilder.Build(cmd);

        Assert.Equal(key1, key2);
    }

    [Fact]
    public void Build_Backtest_Different_Params_Produces_Different_Key()
    {
        var cmd1 = MakeBacktestCommand();
        var cmd2 = MakeBacktestCommand() with { BacktestSettings = MakeBacktestCommand().BacktestSettings with { InitialCash = 99999m } };

        Assert.NotEqual(RunKeyBuilder.Build(cmd1), RunKeyBuilder.Build(cmd2));
    }

    [Fact]
    public void Build_Backtest_Parameter_Order_Independence()
    {
        var cmd1 = MakeBacktestCommand() with
        {
            StrategyParameters = new Dictionary<string, object> { ["a"] = 1, ["b"] = 2 }
        };
        var cmd2 = MakeBacktestCommand() with
        {
            StrategyParameters = new Dictionary<string, object> { ["b"] = 2, ["a"] = 1 }
        };

        Assert.Equal(RunKeyBuilder.Build(cmd1), RunKeyBuilder.Build(cmd2));
    }

    // Phase 4 P4-A removed the original "empty/default TimeFrame normalization" test:
    // `TimeFrame` is now a strict value type and cannot be constructed from "" or "default",
    // so the case the old test guarded is unreachable at the type system. Empty-input
    // behavior is now the responsibility of `TimeFrame.Parse` (throws), which has its own
    // tests in `Domain.Tests/Strategy/TimeFrameTests.cs`.

    [Fact]
    public void Build_Backtest_Returns_SHA256_Hex_Format()
    {
        var key = RunKeyBuilder.Build(MakeBacktestCommand());

        // SHA256 produces 64 hex chars (lowercase)
        Assert.Equal(64, key.Length);
        Assert.Matches("^[0-9a-f]{64}$", key);
    }

    // -----------------------------------------------------------------------
    // Live session fingerprint
    // -----------------------------------------------------------------------

    [Fact]
    public void Build_Live_DeterministicForSameInput()
    {
        var cmd = MakeLiveCommand(new Dictionary<string, object> { ["lookback"] = 14, ["threshold"] = 0.5 });

        var fp1 = LiveRunKeyBuilder.Build(cmd);
        var fp2 = LiveRunKeyBuilder.Build(cmd);

        Assert.Equal(fp1, fp2);
    }

    [Fact]
    public void Build_Live_DiffersForDifferentParams()
    {
        var fp1 = LiveRunKeyBuilder.Build(
            MakeLiveCommand(new Dictionary<string, object> { ["lookback"] = 14 }));
        var fp2 = LiveRunKeyBuilder.Build(
            MakeLiveCommand(new Dictionary<string, object> { ["lookback"] = 20 }));

        Assert.NotEqual(fp1, fp2);
    }

    [Fact]
    public void Build_Live_DiffersForDifferentSubscriptions()
    {
        var cmd1 = MakeLiveCommand() with
        {
            DataSubscriptions = [new TimeBarSubscription("BTCUSDT", "Binance", DataFeedRole.Primary, TimeFrame.Parse("1m"))]
        };
        var cmd2 = MakeLiveCommand() with
        {
            DataSubscriptions = [new TimeBarSubscription("ETHUSDT", "Binance", DataFeedRole.Primary, TimeFrame.Parse("1m"))]
        };

        Assert.NotEqual(LiveRunKeyBuilder.Build(cmd1), LiveRunKeyBuilder.Build(cmd2));
    }

    [Fact]
    public void Build_Live_Returns_SHA256_Hex_Format()
    {
        var fp = LiveRunKeyBuilder.Build(MakeLiveCommand());

        Assert.Equal(64, fp.Length);
        Assert.Matches("^[0-9a-f]{64}$", fp);
    }

    [Fact]
    public void Build_Live_ParameterOrderIndependence()
    {
        var fp1 = LiveRunKeyBuilder.Build(
            MakeLiveCommand(new Dictionary<string, object> { ["a"] = 1, ["b"] = 2 }));
        var fp2 = LiveRunKeyBuilder.Build(
            MakeLiveCommand(new Dictionary<string, object> { ["b"] = 2, ["a"] = 1 }));

        Assert.Equal(fp1, fp2);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static RunBacktestCommand MakeBacktestCommand() => new()
    {
        DataSubscriptions = [new TimeBarSubscription("BTCUSDT", "Binance", DataFeedRole.Primary, TimeFrame.Parse("1h"))],
        BacktestSettings = new BacktestSettingsDto
        {
            InitialCash = 10000m,
            StartTime = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
            EndTime = new DateTimeOffset(2024, 12, 31, 23, 59, 59, TimeSpan.Zero),
            CommissionPerTrade = 0.001m,
            SlippageTicks = 0,
        },
        StrategyName = "SmaCrossover",
        StrategyParameters = new Dictionary<string, object> { ["fastPeriod"] = 10, ["slowPeriod"] = 30 }
    };

    private static StartLiveSessionCommand MakeLiveCommand(
        IDictionary<string, object>? parameters = null) => new()
    {
        StrategyName = "Strat",
        InitialCash = 10000m,
        StrategyParameters = parameters,
        DataSubscriptions = [new TimeBarSubscription("BTCUSDT", "Binance", DataFeedRole.Primary, TimeFrame.Parse("1m"))],
    };

}
