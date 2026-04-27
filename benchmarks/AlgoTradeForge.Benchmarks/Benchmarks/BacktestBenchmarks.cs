using AlgoTradeForge.Benchmarks.Loaders;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.PrevBarBreakout;
using BenchmarkDotNet.Attributes;

namespace AlgoTradeForge.Benchmarks.Benchmarks;

[MemoryDiagnoser]
[Config(typeof(BriefJsonConfig))]
public class BacktestBenchmarks
{
    // Empirical fills-per-bar floor for the symmetric breakout strategy on 5y BTCUSDT 1h.
    // If the run produces materially fewer fills than this, the strategy's order flow is
    // being silently suppressed somewhere in the pipeline (TradeRegistry MaxConcurrentGroups,
    // OrderValidator cash/margin rejection, MoneyManagement returning sub-MinQty, etc.) and
    // the benchmark would be measuring rejection paths instead of real engine work.
    private const double MinExpectedFillsPerBar = 0.5;

    private TimeSeries<Int64Bar>[] _seriesArray = null!;
    private BacktestEngine _engine = null!;
    private BacktestOptions _options = null!;
    private CryptoAsset _btc = null!;
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
            // $10M, deliberately oversized so cash/margin validation never bottlenecks
            // the engine. This is a throughput probe, not a strategy backtest — see the
            // sanity check below for the guard against silent capital-side rejection.
            InitialCash = 10_000_000_00,
            StartTime = DateTimeOffset.MinValue,
            EndTime = DateTimeOffset.MaxValue,
            CommissionPerTrade = 0.001m,
            SlippageTicks = 0,
        };

        SanityCheckFillRate();
    }

    [Benchmark]
    public BacktestResult Backtest_5y_Hourly()
    {
        var sub = new DataSubscription(_btc, TimeSpan.FromHours(1));
        var strategy = new PrevBarBreakoutStrategy(new PrevBarBreakoutParams
        {
            DataSubscriptions = [sub],
            EntryOffsetTicks = 5,
            SlBufferTicks = 5,
            MaxBars = 0, // close on fill bar — keeps order churn high, the point of this benchmark
            AtrPeriod = 14,
            MinVolatilityPct = 0.0, // disabled by default; benchmark exercises happy path
        });
        return _engine.Run(_seriesArray, strategy, _options);
    }

    // Runs the same configuration as the benchmark once and validates the fills/bar
    // ratio. Cheap (~140 ms) compared to the multi-second benchmark, and runs only
    // in [GlobalSetup] so it doesn't pollute the timed region. Throws (failing the
    // benchmark) rather than logging — silent suppression is exactly the failure
    // mode this guards against.
    private void SanityCheckFillRate()
    {
        var result = Backtest_5y_Hourly();
        var fillsPerBar = (double)result.Fills.Count / _barCount;
        Console.WriteLine($"[SanityCheck] {result.Fills.Count} fills / {_barCount} bars = {fillsPerBar:F3} fills/bar.");
        if (fillsPerBar < MinExpectedFillsPerBar)
        {
            throw new InvalidOperationException(
                $"Sanity check failed: {result.Fills.Count} fills over {_barCount} bars " +
                $"= {fillsPerBar:F3} fills/bar (floor {MinExpectedFillsPerBar:F2}). " +
                $"Strategy order flow is being suppressed somewhere — the benchmark would " +
                $"measure rejection paths instead of real engine work. Likely culprits: " +
                $"OrderValidator cash/margin rejection (raise InitialCash), TradeRegistry " +
                $"MaxConcurrentGroups cap, or MoneyManagement returning sub-MinQty.");
        }
    }
}
