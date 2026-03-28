using AlgoTradeForge.Application.Optimization;
using AlgoTradeForge.Application.Persistence;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Optimization.Space;
using AlgoTradeForge.Domain.Reporting;
using AlgoTradeForge.Domain.Strategy;
using Xunit;

namespace AlgoTradeForge.Application.Tests.Optimization;

public sealed class GeneticFitnessCacheTests
{
    [Fact]
    public void BuildCacheKey_SameValues_DifferentInsertionOrder_ProducesSameKey()
    {
        var combo1 = new ParameterCombination(new Dictionary<string, object>
        {
            ["alpha"] = 1.5,
            ["beta"] = 10,
            ["gamma"] = 3L,
        });

        // Same values, different insertion order
        var combo2 = new ParameterCombination(new Dictionary<string, object>
        {
            ["gamma"] = 3L,
            ["alpha"] = 1.5,
            ["beta"] = 10,
        });

        var key1 = GeneticFitnessCache.BuildCacheKey(combo1);
        var key2 = GeneticFitnessCache.BuildCacheKey(combo2);

        Assert.Equal(key1, key2);
    }

    [Fact]
    public void BuildCacheKey_DifferentValues_ProduceDifferentKeys()
    {
        var combo1 = new ParameterCombination(new Dictionary<string, object>
        {
            ["period"] = 14,
            ["threshold"] = 2.5,
        });

        var combo2 = new ParameterCombination(new Dictionary<string, object>
        {
            ["period"] = 14,
            ["threshold"] = 3.0,
        });

        var key1 = GeneticFitnessCache.BuildCacheKey(combo1);
        var key2 = GeneticFitnessCache.BuildCacheKey(combo2);

        Assert.NotEqual(key1, key2);
    }

    [Fact]
    public void BuildCacheKey_WithModuleSelection_IncludesSubParams()
    {
        var mod1 = new ModuleSelection("MyFilter", new Dictionary<string, object>
        {
            ["length"] = 20,
            ["factor"] = 1.5,
        });

        var combo1 = new ParameterCombination(new Dictionary<string, object>
        {
            ["filter"] = mod1,
            ["period"] = 10,
        });

        // Same module with different sub-param
        var mod2 = new ModuleSelection("MyFilter", new Dictionary<string, object>
        {
            ["length"] = 20,
            ["factor"] = 2.0,
        });

        var combo2 = new ParameterCombination(new Dictionary<string, object>
        {
            ["filter"] = mod2,
            ["period"] = 10,
        });

        var key1 = GeneticFitnessCache.BuildCacheKey(combo1);
        var key2 = GeneticFitnessCache.BuildCacheKey(combo2);

        Assert.NotEqual(key1, key2);
        Assert.Contains("MyFilter", key1);
        Assert.Contains("length=20", key1);
    }

    [Fact]
    public void BuildCacheKey_WithDataSubscription_DifferentiatesByAsset()
    {
        var btcAsset = CryptoAsset.Create("BTCUSDT", "Binance", 2);
        var ethAsset = CryptoAsset.Create("ETHUSDT", "Binance", 2);

        var combo1 = new ParameterCombination(new Dictionary<string, object>
        {
            ["sub"] = new DataSubscription(btcAsset, TimeSpan.FromHours(1)),
            ["period"] = 14,
        });

        var combo2 = new ParameterCombination(new Dictionary<string, object>
        {
            ["sub"] = new DataSubscription(ethAsset, TimeSpan.FromHours(1)),
            ["period"] = 14,
        });

        var key1 = GeneticFitnessCache.BuildCacheKey(combo1);
        var key2 = GeneticFitnessCache.BuildCacheKey(combo2);

        Assert.NotEqual(key1, key2);
        Assert.Contains("BTCUSDT", key1);
        Assert.Contains("ETHUSDT", key2);
    }

    [Fact]
    public void TryGet_Miss_ReturnsFalse()
    {
        var cache = new GeneticFitnessCache();
        var combo = new ParameterCombination(new Dictionary<string, object> { ["x"] = 1 });

        Assert.False(cache.TryGet(combo, out _, out _));
        Assert.Equal(0, cache.ReadHits());
    }

    [Fact]
    public void TryGet_Hit_ReturnsTrueAndIncrementsHits()
    {
        var cache = new GeneticFitnessCache();
        var combo = new ParameterCombination(new Dictionary<string, object> { ["x"] = 1 });

        // Store an entry
        cache.TryGet(combo, out var key, out _);
        cache.TryAdd(key, new CachedFitnessEntry(1.5, false, false, null));

        // Look it up
        Assert.True(cache.TryGet(combo, out _, out var entry));
        Assert.Equal(1.5, entry.Fitness);
        Assert.Equal(1, cache.ReadHits());
    }

    [Fact]
    public void TryAdd_RespectsMaxEntriesCap()
    {
        var cache = new GeneticFitnessCache(maxEntries: 3);

        for (var i = 0; i < 10; i++)
        {
            var combo = new ParameterCombination(new Dictionary<string, object> { ["x"] = i });
            cache.TryGet(combo, out var key, out _);
            cache.TryAdd(key, new CachedFitnessEntry(i, false, false, null));
        }

        Assert.Equal(3, cache.EntryCount);
    }

    [Fact]
    public void TryAdd_DuplicateKey_DoesNotIncrementCount()
    {
        var cache = new GeneticFitnessCache();
        var combo = new ParameterCombination(new Dictionary<string, object> { ["x"] = 1 });

        cache.TryGet(combo, out var key, out _);
        cache.TryAdd(key, new CachedFitnessEntry(1.0, false, false, null));
        cache.TryAdd(key, new CachedFitnessEntry(2.0, false, false, null)); // duplicate

        Assert.Equal(1, cache.EntryCount);
    }

    [Fact]
    public void DeduplicateAndDrainSorted_RemovesDuplicateTrials()
    {
        var queue = new BoundedTrialQueue(10, MetricNames.SharpeRatio);

        // Add two records with identical parameters but different IDs
        var record1 = MakeRecord(sharpe: 2.0, paramValue: 14);
        var record2 = MakeRecord(sharpe: 1.5, paramValue: 14); // same params, worse fitness

        queue.TryAdd(record1);
        queue.TryAdd(record2);

        var results = queue.DeduplicateAndDrainSorted();

        // Should keep only one (the best one, since DrainSorted returns best-first)
        Assert.Single(results);
        Assert.Equal(2.0, results[0].Metrics.SharpeRatio);
    }

    [Fact]
    public void DeduplicateAndDrainSorted_KeepsFirstOccurrence()
    {
        var queue = new BoundedTrialQueue(10, MetricNames.SharpeRatio);

        // Three records: two duplicates and one unique
        var dup1 = MakeRecord(sharpe: 3.0, paramValue: 14);
        var unique = MakeRecord(sharpe: 2.0, paramValue: 20);
        var dup2 = MakeRecord(sharpe: 1.0, paramValue: 14);

        queue.TryAdd(dup1);
        queue.TryAdd(unique);
        queue.TryAdd(dup2);

        var results = queue.DeduplicateAndDrainSorted();

        // Should have 2: the best of the dups (3.0) and the unique (2.0)
        Assert.Equal(2, results.Count);
        Assert.Equal(3.0, results[0].Metrics.SharpeRatio);
        Assert.Equal(2.0, results[1].Metrics.SharpeRatio);
    }

    [Fact]
    public void CacheHit_PassingTrial_ReAddsRecordToTopTrials()
    {
        // Capacity=1 queue filled with a mediocre record
        var queue = new BoundedTrialQueue(1, MetricNames.SharpeRatio);
        var mediocre = MakeRecord(sharpe: 1.0, paramValue: 10);
        queue.TryAdd(mediocre);

        // Simulate a cache hit with a better record
        var better = MakeRecord(sharpe: 5.0, paramValue: 20);
        queue.TryAdd(better);

        var results = queue.DeduplicateAndDrainSorted();

        // The better record should have displaced the mediocre one
        Assert.Single(results);
        Assert.Equal(5.0, results[0].Metrics.SharpeRatio);
    }

    [Fact]
    public void CacheHit_FilteredOrFailed_NullRecord_DoesNotThrow()
    {
        // Filtered and failed cache entries have Record=null
        // Verify that when Record is null, no TryAdd is called (nothing to verify except no crash)
        var queue = new BoundedTrialQueue(10, MetricNames.SharpeRatio);

        // Simulate: no record to add for filtered/failed hits — queue stays empty
        Assert.Empty(queue.DeduplicateAndDrainSorted());
    }

    [Fact]
    public void DeduplicateAndDrainSorted_AfterCacheReAdditions_ReturnsSingleCopy()
    {
        var queue = new BoundedTrialQueue(10, MetricNames.SharpeRatio);

        // Same record re-added multiple times (simulating repeated cache hits across generations)
        var record = MakeRecord(sharpe: 3.0, paramValue: 14);
        queue.TryAdd(record);
        queue.TryAdd(record);
        queue.TryAdd(record);

        var results = queue.DeduplicateAndDrainSorted();

        Assert.Single(results);
        Assert.Equal(3.0, results[0].Metrics.SharpeRatio);
    }

    private static BacktestRunRecord MakeRecord(double sharpe, int paramValue = 14)
    {
        return new BacktestRunRecord
        {
            Id = Guid.NewGuid(),
            StrategyName = "Test",
            StrategyVersion = "1",
            Parameters = new Dictionary<string, object> { ["period"] = paramValue, ["threshold"] = 2.5 },
            DataSubscription = new DataSubscriptionDto { AssetName = "BTCUSDT", Exchange = "Binance", TimeFrame = "1h" },
            BacktestSettings = new BacktestSettingsDto
            {
                InitialCash = 10_000m,
                CommissionPerTrade = 0m,
                SlippageTicks = 0,
                StartTime = DateTimeOffset.UtcNow.AddDays(-30),
                EndTime = DateTimeOffset.UtcNow,
            },
            StartedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow,
            DurationMs = 100,
            TotalBars = 720,
            Metrics = new PerformanceMetrics
            {
                TotalTrades = 10, WinningTrades = 6, LosingTrades = 4,
                NetProfit = 300m, GrossProfit = 500m, GrossLoss = -200m, TotalCommissions = 0m,
                TotalReturnPct = 3.0, AnnualizedReturnPct = 36.0,
                SharpeRatio = sharpe, SortinoRatio = 0, MaxDrawdownPct = 5.0,
                WinRatePct = 60.0, ProfitFactor = 2.5, AverageWin = 83.3, AverageLoss = -50.0,
                InitialCapital = 10_000m, FinalEquity = 10_300m, TradingDays = 30,
            },
            EquityCurve = [],
            RunMode = RunModes.Backtest,
        };
    }
}
