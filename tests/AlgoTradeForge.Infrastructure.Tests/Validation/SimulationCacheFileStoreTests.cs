using AlgoTradeForge.Application;
using AlgoTradeForge.Application.Persistence;
using AlgoTradeForge.Application.Validation;
using AlgoTradeForge.Domain.Reporting;
using AlgoTradeForge.Domain.Validation;
using AlgoTradeForge.Storage;
using AlgoTradeForge.Infrastructure.Validation;
using Xunit;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.Subscriptions;

namespace AlgoTradeForge.Infrastructure.Tests.Validation;

public class SimulationCacheFileStoreTests : IDisposable
{
    private readonly string _testDir = Path.Combine(Path.GetTempPath(), $"cache_test_{Guid.NewGuid():N}");
    private readonly SimulationCacheFileStore _store = new(new LocalFileStorage());

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
            Directory.Delete(_testDir, true);
    }

    [Fact]
    public async Task WriteAndRead_RoundTrip_PreservesData()
    {
        var cache = CreateTestCache(trialCount: 10, barCount: 50);
        var filePath = Path.Combine(_testDir, "test.bin");

        await _store.Write(cache, filePath, TestContext.Current.CancellationToken);
        Assert.True(File.Exists(filePath));

        var loaded = await _store.Read(filePath, TestContext.Current.CancellationToken);

        Assert.Equal(cache.TrialCount, loaded.TrialCount);
        Assert.Equal(cache.MaxBarCount, loaded.MaxBarCount);

        for (var t = 0; t < cache.TrialCount; t++)
        {
            Assert.Equal(cache.GetBarCount(t), loaded.GetBarCount(t));

            var origTs = cache.GetTrialTimestamps(t);
            var loadedTs = loaded.GetTrialTimestamps(t);
            for (var b = 0; b < cache.GetBarCount(t); b++)
                Assert.Equal(origTs[b], loadedTs[b]);

            var origPnl = cache.GetTrialPnl(t);
            var loadedPnl = loaded.GetTrialPnl(t);
            for (var b = 0; b < cache.GetBarCount(t); b++)
                Assert.Equal(origPnl[b], loadedPnl[b]);
        }
    }

    [Fact]
    public async Task WriteDirect_RoundTrip_MatchesBuild()
    {
        var trials = CreateTestTrials(trialCount: 5, barCount: 50);
        var filePath = Path.Combine(_testDir, "direct.bin");

        await _store.WriteDirect(trials, filePath, TestContext.Current.CancellationToken);
        var loaded = await _store.Read(filePath, TestContext.Current.CancellationToken);

        var expected = SimulationCacheBuilder.Build(trials);

        Assert.Equal(expected.TrialCount, loaded.TrialCount);

        for (var t = 0; t < expected.TrialCount; t++)
        {
            Assert.Equal(expected.GetBarCount(t), loaded.GetBarCount(t));

            var origTs = expected.GetTrialTimestamps(t);
            var loadedTs = loaded.GetTrialTimestamps(t);
            for (var b = 0; b < expected.GetBarCount(t); b++)
                Assert.Equal(origTs[b], loadedTs[b]);

            var orig = expected.GetTrialPnl(t);
            var disk = loaded.GetTrialPnl(t);
            for (var b = 0; b < expected.GetBarCount(t); b++)
                Assert.Equal(orig[b], disk[b], precision: 10);
        }
    }

    [Fact]
    public async Task WriteAndRead_SingleTrialSingleBar()
    {
        var cache = CreateTestCache(trialCount: 1, barCount: 1);
        var filePath = Path.Combine(_testDir, "minimal.bin");

        await _store.Write(cache, filePath, TestContext.Current.CancellationToken);
        var loaded = await _store.Read(filePath, TestContext.Current.CancellationToken);

        Assert.Equal(1, loaded.TrialCount);
        Assert.Equal(1, loaded.GetBarCount(0));
        Assert.Equal(cache.GetTrialTimestamps(0)[0], loaded.GetTrialTimestamps(0)[0]);
        Assert.Equal(cache.GetTrialPnl(0)[0], loaded.GetTrialPnl(0)[0]);
    }

    [Fact]
    public async Task WriteAndRead_VariableLengthTrials()
    {
        var timelines = new long[][] { [100, 200, 300], [100, 200] };
        var trials = new TrialData[] { new(0, [1.0, 2.0, 3.0]), new(1, [-1.0, 0.5]) };
        var cache = new SimulationCache(timelines, trials);
        var filePath = Path.Combine(_testDir, "variable.bin");

        await _store.Write(cache, filePath, TestContext.Current.CancellationToken);
        var loaded = await _store.Read(filePath, TestContext.Current.CancellationToken);

        Assert.Equal(2, loaded.TrialCount);
        Assert.Equal(3, loaded.GetBarCount(0));
        Assert.Equal(2, loaded.GetBarCount(1));
        Assert.Equal(3, loaded.MaxBarCount);

        Assert.Equal(300L, loaded.GetTrialTimestamps(0)[2]);
        Assert.Equal(3.0, loaded.GetTrialPnl(0)[2]);

        Assert.Equal(200L, loaded.GetTrialTimestamps(1)[1]);
        Assert.Equal(0.5, loaded.GetTrialPnl(1)[1]);
    }

    [Fact]
    public async Task Read_UnknownVersion_Throws()
    {
        var filePath = Path.Combine(_testDir, "badversion.bin");
        Directory.CreateDirectory(_testDir);

        using (var fs = new FileStream(filePath, FileMode.Create))
        using (var writer = new BinaryWriter(fs))
        {
            writer.Write(999);
            writer.Write(1);
        }

        var ex = await Assert.ThrowsAsync<InvalidDataException>(
            () => _store.Read(filePath, TestContext.Current.CancellationToken));
        Assert.Contains("999", ex.Message);
        Assert.Contains("3", ex.Message);
    }

    [Fact]
    public async Task WriteAndRead_V3_SharedTimeline_RoundTrip()
    {
        var timestamps = new long[] { 1000, 2000, 3000, 4000, 5000 };
        var matrix = new double[5][];
        var rng = new Random(123);
        for (var t = 0; t < 5; t++)
        {
            matrix[t] = new double[5];
            for (var b = 0; b < 5; b++)
                matrix[t][b] = (rng.NextDouble() - 0.5) * 10;
        }

        var trialData = new TrialData[5];
        for (var t = 0; t < 5; t++)
            trialData[t] = new TrialData(0, matrix[t]);
        var cache = new SimulationCache([timestamps], trialData);
        Assert.Equal(1, cache.TimelineCount);

        var filePath = Path.Combine(_testDir, "v3_shared.bin");
        await _store.Write(cache, filePath, TestContext.Current.CancellationToken);
        var loaded = await _store.Read(filePath, TestContext.Current.CancellationToken);

        Assert.Equal(1, loaded.TimelineCount);
        Assert.Equal(5, loaded.TrialCount);

        for (var t = 0; t < 5; t++)
        {
            Assert.Equal(0, loaded.GetTimelineIndex(t));

            var origTs = cache.GetTrialTimestamps(t);
            var loadedTs = loaded.GetTrialTimestamps(t);
            for (var b = 0; b < 5; b++)
                Assert.Equal(origTs[b], loadedTs[b]);

            var origPnl = cache.GetTrialPnl(t);
            var loadedPnl = loaded.GetTrialPnl(t);
            for (var b = 0; b < 5; b++)
                Assert.Equal(origPnl[b], loadedPnl[b]);
        }
    }

    private static SimulationCache CreateTestCache(int trialCount, int barCount)
    {
        var rng = new Random(42);
        var timestamps = new long[barCount];
        var matrix = new double[trialCount][];

        for (var b = 0; b < barCount; b++)
            timestamps[b] = 1704067200000L + b * 60_000L;

        for (var t = 0; t < trialCount; t++)
        {
            var row = new double[barCount];
            for (var b = 0; b < barCount; b++)
                row[b] = (rng.NextDouble() - 0.5) * 100;

            matrix[t] = row;
        }

        var trialData = new TrialData[trialCount];
        for (var t = 0; t < trialCount; t++)
            trialData[t] = new TrialData(0, matrix[t]);
        return new SimulationCache([timestamps], trialData);
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
                DataSubscriptions = [new TimeBarSubscription("TEST", "Binance", DataFeedRole.Primary, TimeFrame.Parse("1m"))],
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
