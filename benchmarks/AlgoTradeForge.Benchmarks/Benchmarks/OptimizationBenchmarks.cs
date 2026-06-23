using AlgoTradeForge.Domain.Strategy.Subscriptions;
using System.Collections.Concurrent;
using AlgoTradeForge.Benchmarks.Loaders;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.PrevBarBreakout;
using BenchmarkDotNet.Attributes;

namespace AlgoTradeForge.Benchmarks.Benchmarks;

/// <summary>
/// Mirrors the per-DSS loop shape of OptimizationTaskExecutor: partition the
/// combination stream across <see cref="Environment.ProcessorCount"/> workers,
/// each running one engine.Run() per combination. No DI, no progress cache,
/// no DB — just the parallel trial loop, so we can attribute regressions to
/// the engine/strategy/registry layer rather than the surrounding plumbing.
/// </summary>
[MemoryDiagnoser]
[Config(typeof(BriefJsonConfig))]
public class OptimizationBenchmarks
{
    // 10 × 10 × 10 = 1000 trials
    private const int OffsetSteps = 10;
    private const int SlSteps = 10;
    private const int MaxBarsSteps = 10;

    // Same intent as BacktestBenchmarks.MinExpectedFillsPerBar: if combos run dry the
    // measurement is dominated by rejection paths, not real engine work. 0.25 is laxer
    // than the single-config backtest because some grid corners (high SlBufferTicks +
    // MaxBars=0) genuinely reject more orders.
    private const double MinExpectedFillsPerBarAvg = 0.25;

    private TimeSeries<Int64Bar>[] _seriesArray = null!;
    private BacktestEngine _engine = null!;
    private BacktestOptions _options = null!;
    private CryptoAsset _btc = null!;
    private List<PrevBarBreakoutParams> _combinations = null!;
    private int _barCount;

    [GlobalSetup]
    public void Setup()
    {
        var bars = BundledCandleLoader.LoadBtcUsdt1hFiveYears();
        _seriesArray = [bars];
        _barCount = bars.Count;

        _btc = CryptoAsset.Create(
            name: "BTCUSDT",
            exchange: "binance",
            decimalDigits: 2,
            minOrderQuantity: 0.00001m,
            quantityStepSize: 0.00001m);

        _engine = new BacktestEngine(new BarMatcher(), new OrderValidator());

        _options = new BacktestOptions
        {
            // $10M (cents-as-long) — matches BacktestBenchmarks. Earlier value of
            // 10_000_00 was $10k, which silently rejected most fills on BTC during
            // 2020-2024 and made the benchmark measure the OrderValidator reject path.
            InitialCash = 10_000_000_00,
            StartTime = DateTimeOffset.MinValue,
            EndTime = DateTimeOffset.MaxValue,
            CommissionPerTrade = 0.001m,
            SlippageTicks = 0,
        };

        _combinations = new List<PrevBarBreakoutParams>(OffsetSteps * SlSteps * MaxBarsSteps);
        for (var o = 0; o < OffsetSteps; o++)
        for (var s = 0; s < SlSteps; s++)
        for (var b = 0; b < MaxBarsSteps; b++)
        {
            // DataSubscriptions stays null on the template; the worker loop attaches a
            // fresh subscription per trial because that's what production optimization
            // does (each trial constructs a fresh strategy instance) and we want the
            // measured allocations to reflect that.
            _combinations.Add(new PrevBarBreakoutParams
            {
                EntryOffsetTicks = o,
                SlBufferTicks = s,
                MaxBars = b, // 0..9: spans 1-bar momentum to 10-bar holds
                AtrPeriod = 14,
                MinVolatilityPct = 0.0,
            });
        }

        SanityCheckMedianTrial();
    }

    // Cheap (~140 ms) — runs one trial with median grid params and asserts a non-trivial
    // fill rate. Same shape as BacktestBenchmarks.SanityCheckFillRate, scoped to
    // [GlobalSetup] so it stays out of the timed region. Without this, a config
    // regression (e.g. capital sized too small) would silently turn 1000 trials into
    // 1000 reject-path runs and the Mean would still look "stable".
    private void SanityCheckMedianTrial()
    {
        var sub = SubscriptionResolver.Resolve(new TimeBarSubscription(_btc.Name, _btc.Exchange, DataFeedRole.Primary, new TimeFrame(TimeSpan.FromHours(1))), _btc);
        var trialParams = new PrevBarBreakoutParams
        {
            DataSubscriptions = [sub],
            EntryOffsetTicks = OffsetSteps / 2,
            SlBufferTicks = SlSteps / 2,
            MaxBars = MaxBarsSteps / 2,
            AtrPeriod = 14,
            MinVolatilityPct = 0.0,
        };
        var strategy = new PrevBarBreakoutStrategy(trialParams);
        var result = _engine.Run(_seriesArray, strategy, _options);
        var fillsPerBar = (double)result.Fills.Count / _barCount;
        Console.WriteLine($"[SanityCheck/Opt] median trial: {result.Fills.Count} fills / {_barCount} bars = {fillsPerBar:F3} fills/bar.");
        if (fillsPerBar < MinExpectedFillsPerBarAvg)
        {
            throw new InvalidOperationException(
                $"OptimizationBenchmarks sanity check failed: median trial only produced " +
                $"{fillsPerBar:F3} fills/bar (floor {MinExpectedFillsPerBarAvg:F2}). " +
                $"The grid is likely measuring rejection paths. Check InitialCash, " +
                $"OrderValidator behavior, or the strategy's MoneyManagement output.");
        }
    }

    [Benchmark]
    public long Optimization_1000Trials_Parallel()
    {
        var maxParallelism = Environment.ProcessorCount;
        var totalFills = 0L;

        var partitions = Partitioner.Create(_combinations, EnumerablePartitionerOptions.NoBuffering)
            .GetPartitions(maxParallelism);
        var tasks = new Task[partitions.Count];

        for (var p = 0; p < partitions.Count; p++)
        {
            var partition = partitions[p];
            tasks[p] = Task.Factory.StartNew(() =>
            {
                long localFills = 0;
                using (partition)
                {
                    while (partition.MoveNext())
                    {
                        var template = partition.Current;
                        var sub = SubscriptionResolver.Resolve(new TimeBarSubscription(_btc.Name, _btc.Exchange, DataFeedRole.Primary, new TimeFrame(TimeSpan.FromHours(1))), _btc);
                        var trialParams = new PrevBarBreakoutParams
                        {
                            DataSubscriptions = [sub],
                            EntryOffsetTicks = template.EntryOffsetTicks,
                            SlBufferTicks = template.SlBufferTicks,
                            MaxBars = template.MaxBars,
                            AtrPeriod = template.AtrPeriod,
                            MinVolatilityPct = template.MinVolatilityPct,
                        };
                        var strategy = new PrevBarBreakoutStrategy(trialParams);
                        var result = _engine.Run(_seriesArray, strategy, _options);
                        localFills += result.Fills.Count;
                    }
                }
                Interlocked.Add(ref totalFills, localFills);
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        Task.WaitAll(tasks);
        return totalFills;
    }
}
