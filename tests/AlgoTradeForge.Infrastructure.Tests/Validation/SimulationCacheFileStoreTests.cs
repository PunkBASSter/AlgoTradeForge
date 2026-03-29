using AlgoTradeForge.Application;
using AlgoTradeForge.Application.Persistence;
using AlgoTradeForge.Application.Validation;
using AlgoTradeForge.Domain.Reporting;
using AlgoTradeForge.Domain.Validation;
using AlgoTradeForge.Infrastructure.Validation;
using Xunit;

namespace AlgoTradeForge.Infrastructure.Tests.Validation;

public class SimulationCacheFileStoreTests : IDisposable
{
    private readonly string _testDir = Path.Combine(Path.GetTempPath(), $"cache_test_{Guid.NewGuid():N}");
    private readonly SimulationCacheFileStore _store = new();

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
            Directory.Delete(_testDir, true);
    }

    [Fact]
    public void WriteAndRead_RoundTrip_PreservesData()
    {
        var cache = CreateTestCache(trialCount: 10, barCount: 50);
        var filePath = Path.Combine(_testDir, "test.bin");

        _store.Write(cache, filePath);
        Assert.True(File.Exists(filePath));

        var loaded = _store.Read(filePath);

        Assert.Equal(cache.TrialCount, loaded.TrialCount);
        Assert.Equal(cache.BarCount, loaded.BarCount);

        // Verify timestamps
        for (var i = 0; i < cache.BarCount; i++)
            Assert.Equal(cache.BarTimestamps[i], loaded.BarTimestamps[i]);

        // Verify P&L matrix
        for (var t = 0; t < cache.TrialCount; t++)
        {
            var original = cache.GetTrialPnl(t);
            var roundTrip = loaded.GetTrialPnl(t);
            for (var b = 0; b < cache.BarCount; b++)
                Assert.Equal(original[b], roundTrip[b]);
        }
    }

    [Fact]
    public void WriteDirect_RoundTrip_MatchesBuild()
    {
        var trials = CreateTestTrials(trialCount: 5, barCount: 50);
        var filePath = Path.Combine(_testDir, "direct.bin");

        _store.WriteDirect(trials, filePath);
        var loaded = _store.Read(filePath);

        // Compare against the in-memory Build path
        var expected = SimulationCacheBuilder.Build(trials);

        Assert.Equal(expected.TrialCount, loaded.TrialCount);
        Assert.Equal(expected.BarCount, loaded.BarCount);

        for (var i = 0; i < expected.BarCount; i++)
            Assert.Equal(expected.BarTimestamps[i], loaded.BarTimestamps[i]);

        for (var t = 0; t < expected.TrialCount; t++)
        {
            var orig = expected.GetTrialPnl(t);
            var disk = loaded.GetTrialPnl(t);
            for (var b = 0; b < expected.BarCount; b++)
                Assert.Equal(orig[b], disk[b], precision: 10);
        }
    }

    [Fact]
    public void WriteAndRead_SingleTrialSingleBar()
    {
        var cache = CreateTestCache(trialCount: 1, barCount: 1);
        var filePath = Path.Combine(_testDir, "minimal.bin");

        _store.Write(cache, filePath);
        var loaded = _store.Read(filePath);

        Assert.Equal(1, loaded.TrialCount);
        Assert.Equal(1, loaded.BarCount);
        Assert.Equal(cache.BarTimestamps[0], loaded.BarTimestamps[0]);
        Assert.Equal(cache.GetTrialPnl(0)[0], loaded.GetTrialPnl(0)[0]);
    }

    [Fact]
    public void SliceWindow_WorksOnLoadedCache()
    {
        var cache = CreateTestCache(trialCount: 5, barCount: 100);
        var filePath = Path.Combine(_testDir, "slice.bin");

        _store.Write(cache, filePath);
        var loaded = _store.Read(filePath);

        var sliced = loaded.SliceWindow(10, 50);

        Assert.Equal(5, sliced.TrialCount);
        Assert.Equal(40, sliced.BarCount);

        // Verify sliced timestamps match
        for (var i = 0; i < 40; i++)
            Assert.Equal(cache.BarTimestamps[10 + i], sliced.BarTimestamps[i]);
    }

    private static SimulationCache CreateTestCache(int trialCount, int barCount)
    {
        var rng = new Random(42);
        var timestamps = new long[barCount];
        for (var i = 0; i < barCount; i++)
            timestamps[i] = 1704067200000L + i * 60_000L;

        var matrix = new double[trialCount][];
        for (var t = 0; t < trialCount; t++)
        {
            var row = new double[barCount];
            for (var b = 0; b < barCount; b++)
                row[b] = (rng.NextDouble() - 0.5) * 100;
            matrix[t] = row;
        }

        return new SimulationCache(timestamps, matrix);
    }

    private static List<BacktestRunRecord> CreateTestTrials(int trialCount, int barCount)
    {
        var rng = new Random(42);
        const decimal initialCapital = 10_000m;
        var trials = new List<BacktestRunRecord>(trialCount);

        for (var t = 0; t < trialCount; t++)
        {
            var curve = new List<EquityPoint>(barCount);
            var equity = (double)initialCapital;
            for (var b = 0; b < barCount; b++)
            {
                equity += (rng.NextDouble() - 0.5) * 100;
                curve.Add(new EquityPoint(1704067200000L + b * 60_000L, equity));
            }

            trials.Add(new BacktestRunRecord
            {
                Id = Guid.NewGuid(),
                StrategyName = "Test",
                StrategyVersion = "1.0",
                Parameters = new Dictionary<string, object>(),
                DataSubscription = new DataSubscriptionDto
                {
                    AssetName = "TEST",
                    Exchange = "Binance",
                    TimeFrame = "1m",
                },
                BacktestSettings = new BacktestSettingsDto
                {
                    InitialCash = initialCapital,
                    StartTime = DateTimeOffset.UtcNow.AddDays(-30),
                    EndTime = DateTimeOffset.UtcNow,
                },
                StartedAt = DateTimeOffset.UtcNow,
                CompletedAt = DateTimeOffset.UtcNow,
                DurationMs = 100,
                TotalBars = barCount,
                Metrics = new PerformanceMetrics
                {
                    TotalTrades = 10,
                    WinningTrades = 6,
                    LosingTrades = 4,
                    NetProfit = 100m,
                    GrossProfit = 200m,
                    GrossLoss = -100m,
                    TotalCommissions = 5m,
                    TotalReturnPct = 1,
                    AnnualizedReturnPct = 5,
                    SharpeRatio = 1.0,
                    SortinoRatio = 1.5,
                    MaxDrawdownPct = 10,
                    WinRatePct = 60,
                    ProfitFactor = 2.0,
                    AverageWin = 33,
                    AverageLoss = -25,
                    InitialCapital = initialCapital,
                    FinalEquity = initialCapital + 100m,
                    TradingDays = 30,
                },
                EquityCurve = curve,
                RunMode = "Backtest",
            });
        }

        return trials;
    }
}
