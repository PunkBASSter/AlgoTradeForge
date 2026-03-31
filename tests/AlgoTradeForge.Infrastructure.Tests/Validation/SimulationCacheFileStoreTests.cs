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
        Assert.Equal(cache.MaxBarCount, loaded.MaxBarCount);

        // Verify per-trial timestamps and P&L
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
    public void WriteDirect_RoundTrip_MatchesBuild()
    {
        var trials = CreateTestTrials(trialCount: 5, barCount: 50);
        var filePath = Path.Combine(_testDir, "direct.bin");

        _store.WriteDirect(trials, filePath);
        var loaded = _store.Read(filePath);

        // Compare against the in-memory Build path
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
    public void WriteAndRead_SingleTrialSingleBar()
    {
        var cache = CreateTestCache(trialCount: 1, barCount: 1);
        var filePath = Path.Combine(_testDir, "minimal.bin");

        _store.Write(cache, filePath);
        var loaded = _store.Read(filePath);

        Assert.Equal(1, loaded.TrialCount);
        Assert.Equal(1, loaded.GetBarCount(0));
        Assert.Equal(cache.GetTrialTimestamps(0)[0], loaded.GetTrialTimestamps(0)[0]);
        Assert.Equal(cache.GetTrialPnl(0)[0], loaded.GetTrialPnl(0)[0]);
    }

    [Fact]
    public void WriteAndRead_VariableLengthTrials()
    {
        var timestamps = new long[][] { [100, 200, 300], [100, 200] };
        var matrix = new double[][] { [1.0, 2.0, 3.0], [-1.0, 0.5] };
        var cache = new SimulationCache(timestamps, matrix);
        var filePath = Path.Combine(_testDir, "variable.bin");

        _store.Write(cache, filePath);
        var loaded = _store.Read(filePath);

        Assert.Equal(2, loaded.TrialCount);
        Assert.Equal(3, loaded.GetBarCount(0));
        Assert.Equal(2, loaded.GetBarCount(1));
        Assert.Equal(3, loaded.MaxBarCount);

        // Verify trial 0
        Assert.Equal(300L, loaded.GetTrialTimestamps(0)[2]);
        Assert.Equal(3.0, loaded.GetTrialPnl(0)[2]);

        // Verify trial 1
        Assert.Equal(200L, loaded.GetTrialTimestamps(1)[1]);
        Assert.Equal(0.5, loaded.GetTrialPnl(1)[1]);
    }

    [Fact]
    public void Read_UnknownVersion_Throws()
    {
        var filePath = Path.Combine(_testDir, "badversion.bin");
        Directory.CreateDirectory(_testDir);

        using (var fs = new FileStream(filePath, FileMode.Create))
        using (var writer = new BinaryWriter(fs))
        {
            writer.Write(999); // unsupported version
            writer.Write(1);   // trialCount
        }

        var ex = Assert.Throws<InvalidDataException>(() => _store.Read(filePath));
        Assert.Contains("999", ex.Message);
    }

    private static SimulationCache CreateTestCache(int trialCount, int barCount)
    {
        var rng = new Random(42);
        var timestamps = new long[trialCount][];
        var matrix = new double[trialCount][];

        for (var t = 0; t < trialCount; t++)
        {
            var ts = new long[barCount];
            var row = new double[barCount];
            for (var b = 0; b < barCount; b++)
            {
                ts[b] = 1704067200000L + b * 60_000L;
                row[b] = (rng.NextDouble() - 0.5) * 100;
            }

            timestamps[t] = ts;
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
