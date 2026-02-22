using AlgoTradeForge.Application.Backtests;
using AlgoTradeForge.Application.Optimization;
using AlgoTradeForge.Application.Progress;
using Xunit;

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
        var cmd2 = MakeBacktestCommand() with { InitialCash = 99999m };

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

    [Fact]
    public void Build_Backtest_Returns_SHA256_Hex_Format()
    {
        var key = RunKeyBuilder.Build(MakeBacktestCommand());

        // SHA256 produces 64 hex chars (lowercase)
        Assert.Equal(64, key.Length);
        Assert.Matches("^[0-9a-f]{64}$", key);
    }

    [Fact]
    public void Build_Optimization_Identical_Params_Produces_Same_Key()
    {
        var cmd = MakeOptimizationCommand();
        var key1 = RunKeyBuilder.Build(cmd);
        var key2 = RunKeyBuilder.Build(cmd);

        Assert.Equal(key1, key2);
    }

    [Fact]
    public void Build_Optimization_Different_Params_Produces_Different_Key()
    {
        var cmd1 = MakeOptimizationCommand();
        var cmd2 = MakeOptimizationCommand() with { StrategyName = "OtherStrategy" };

        Assert.NotEqual(RunKeyBuilder.Build(cmd1), RunKeyBuilder.Build(cmd2));
    }

    [Fact]
    public void Build_Optimization_Returns_SHA256_Hex_Format()
    {
        var key = RunKeyBuilder.Build(MakeOptimizationCommand());

        Assert.Equal(64, key.Length);
        Assert.Matches("^[0-9a-f]{64}$", key);
    }

    private static RunBacktestCommand MakeBacktestCommand() => new()
    {
        AssetName = "BTCUSDT",
        Exchange = "Binance",
        StrategyName = "SmaCrossover",
        InitialCash = 10000m,
        StartTime = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
        EndTime = new DateTimeOffset(2024, 12, 31, 23, 59, 59, TimeSpan.Zero),
        CommissionPerTrade = 0.001m,
        SlippageTicks = 0,
        TimeFrame = TimeSpan.FromHours(1),
        StrategyParameters = new Dictionary<string, object> { ["fastPeriod"] = 10, ["slowPeriod"] = 30 }
    };

    private static RunOptimizationCommand MakeOptimizationCommand() => new()
    {
        StrategyName = "SmaCrossover",
        InitialCash = 10000m,
        StartTime = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
        EndTime = new DateTimeOffset(2024, 12, 31, 23, 59, 59, TimeSpan.Zero),
        DataSubscriptions =
        [
            new DataSubscriptionDto { Asset = "BTCUSDT", Exchange = "Binance", TimeFrame = "1:00:00" }
        ]
    };
}
