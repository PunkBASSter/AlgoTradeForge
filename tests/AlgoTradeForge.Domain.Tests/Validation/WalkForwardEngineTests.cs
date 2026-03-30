using AlgoTradeForge.Domain.Validation;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Validation;

public class WalkForwardEngineTests
{

    [Fact]
    public void RunWfo_AscendingEquity_HighWfe()
    {
        // All trials ascending → IS and OOS both positive → WFE near 1.0
        var barCount = 100;
        var trialCount = 5;
        var matrix = new double[trialCount][];
        for (var t = 0; t < trialCount; t++)
        {
            matrix[t] = new double[barCount];
            for (var b = 0; b < barCount; b++)
                matrix[t][b] = 10.0 + t; // Higher trial index = higher P&L per bar
        }

        var timestamps = new long[barCount];
        for (var b = 0; b < barCount; b++)
            timestamps[b] = b * 86400000L;

        var cache = new SimulationCache(timestamps, matrix);
        var config = new WfoConfig
        {
            WindowCount = 5,
            OosPct = 0.20,
            MinWfe = 0.50,
            AnnualizationFactor = 365,
        };

        var result = WalkForwardEngine.RunWfo(cache, config, 10000, TestContext.Current.CancellationToken);

        Assert.Equal(5, result.Windows.Count);
        Assert.True(result.WalkForwardEfficiency > 0.5, $"WFE was {result.WalkForwardEfficiency}");
        Assert.Equal(1.0, result.ProfitableWindowsPct);
        Assert.True(result.Passed);
    }

    [Fact]
    public void RunWfo_ZeroIsReturn_WfeIsZero()
    {
        // IS region has zero P&L → WFE should be 0
        var barCount = 100;
        var matrix = new double[3][];
        for (var t = 0; t < 3; t++)
        {
            matrix[t] = new double[barCount];
            // First 80% (IS) is zero, last 20% (OOS) has positive P&L
            for (var b = (int)(barCount * 0.8); b < barCount; b++)
                matrix[t][b] = 10.0;
        }

        var timestamps = new long[barCount];
        for (var b = 0; b < barCount; b++)
            timestamps[b] = b * 86400000L;

        var cache = new SimulationCache(timestamps, matrix);
        var config = new WfoConfig
        {
            WindowCount = 1,
            OosPct = 0.20,
            MinWfe = 0.50,
            AnnualizationFactor = 365,
        };

        var result = WalkForwardEngine.RunWfo(cache, config, 10000, TestContext.Current.CancellationToken);

        Assert.Single(result.Windows);
        Assert.Equal(0, result.Windows[0].WalkForwardEfficiency);
    }

    [Fact]
    public void RunWfo_TooFewBars_ReturnsFailed()
    {
        // 5 bars split into 10 windows → < 1 bar per window
        var cache = new SimulationCache(
            [1, 2, 3, 4, 5],
            [new double[] { 1, 1, 1, 1, 1 }]);

        var config = new WfoConfig
        {
            WindowCount = 10,
            OosPct = 0.20,
            MinWfe = 0.50,
        };

        var result = WalkForwardEngine.RunWfo(cache, config, 10000, TestContext.Current.CancellationToken);

        Assert.False(result.Passed);
    }

    [Fact]
    public void RunWfo_Cancellation_ThrowsOperationCancelled()
    {
        var cache = new SimulationCache(
            Enumerable.Range(0, 100).Select(i => (long)i).ToArray(),
            [Enumerable.Range(0, 100).Select(i => 10.0).ToArray()]);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var config = new WfoConfig
        {
            WindowCount = 5,
            OosPct = 0.20,
            MinWfe = 0.50,
        };

        Assert.Throws<OperationCanceledException>(() =>
            WalkForwardEngine.RunWfo(cache, config, 10000, cts.Token));
    }

    [Fact]
    public void RunWfm_CorrectGridDimensions()
    {
        var barCount = 200;
        var trialCount = 3;
        var matrix = new double[trialCount][];
        for (var t = 0; t < trialCount; t++)
        {
            matrix[t] = new double[barCount];
            for (var b = 0; b < barCount; b++)
                matrix[t][b] = 5.0;
        }

        var timestamps = new long[barCount];
        for (var b = 0; b < barCount; b++)
            timestamps[b] = b * 86400000L;

        var cache = new SimulationCache(timestamps, matrix);

        var config = new WfmConfig
        {
            PeriodCounts = [4, 6, 8],
            OosPcts = [0.15, 0.20],
            MinWfe = 0.30,
            MinContiguousRows = 2,
            MinContiguousCols = 2,
            MinCellsPassing = 3,
            AnnualizationFactor = 365,
        };

        var result = WalkForwardEngine.RunWfm(cache, config, 10000, TestContext.Current.CancellationToken);

        Assert.Equal(3, result.Grid.Length); // 3 period counts
        Assert.Equal(2, result.Grid[0].Length); // 2 OOS pcts
        Assert.Equal(3, result.PeriodCounts.Length);
        Assert.Equal(2, result.OosPcts.Length);
    }

    [Fact]
    public void RunWfm_AllPassing_FindsCluster()
    {
        var barCount = 300;
        var trialCount = 5;
        var matrix = new double[trialCount][];
        for (var t = 0; t < trialCount; t++)
        {
            matrix[t] = new double[barCount];
            for (var b = 0; b < barCount; b++)
                matrix[t][b] = 10.0 + t * 2.0; // Consistently positive
        }

        var timestamps = new long[barCount];
        for (var b = 0; b < barCount; b++)
            timestamps[b] = b * 86400000L;

        var cache = new SimulationCache(timestamps, matrix);

        var config = new WfmConfig
        {
            PeriodCounts = [4, 6, 8],
            OosPcts = [0.15, 0.20, 0.25],
            MinWfe = 0.30,
            MinContiguousRows = 2,
            MinContiguousCols = 2,
            MinCellsPassing = 3,
            AnnualizationFactor = 365,
        };

        var result = WalkForwardEngine.RunWfm(cache, config, 10000, TestContext.Current.CancellationToken);

        Assert.True(result.ClusterPassCount > 0);
        Assert.NotNull(result.LargestContiguousCluster);
        Assert.NotNull(result.OptimalReoptPeriod);
    }

    [Fact]
    public void RunWfo_WindowSplits_AreCorrect()
    {
        // 100 bars, 4 windows → 25 bars each, 80% IS / 20% OOS
        var barCount = 100;
        var matrix = new double[2][];
        for (var t = 0; t < 2; t++)
        {
            matrix[t] = new double[barCount];
            for (var b = 0; b < barCount; b++)
                matrix[t][b] = 1.0;
        }

        var timestamps = new long[barCount];
        for (var b = 0; b < barCount; b++)
            timestamps[b] = b;

        var cache = new SimulationCache(timestamps, matrix);
        var config = new WfoConfig
        {
            WindowCount = 4,
            OosPct = 0.20,
            MinWfe = 0.0, // Don't filter
        };

        var result = WalkForwardEngine.RunWfo(cache, config, 10000, TestContext.Current.CancellationToken);

        Assert.Equal(4, result.Windows.Count);

        // First window: bars 0-24, IS: 0-19, OOS: 20-24
        Assert.Equal(0, result.Windows[0].IsStartBar);
        Assert.Equal(20, result.Windows[0].IsEndBar);
        Assert.Equal(20, result.Windows[0].OosStartBar);
        Assert.Equal(25, result.Windows[0].OosEndBar);
    }
}
