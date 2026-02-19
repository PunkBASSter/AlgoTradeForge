using AlgoTradeForge.Domain.Reporting;
using AlgoTradeForge.Domain.Tests.TestUtilities;
using AlgoTradeForge.Domain.Trading;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Reporting;

public class MetricsCalculatorTests
{
    private readonly MetricsCalculator _sut = new();

    private static readonly DateTimeOffset Start = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset TwoYearsLater = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TradingDays_DerivedFromDateRange_NotEquityCurveCount()
    {
        // Simulate 1-minute bars over ~2 years (huge curve count)
        var curveCount = 1_000_000;
        var equityCurve = Enumerable.Repeat(10_000m, curveCount).ToList();
        var fills = new List<Fill>();

        var metrics = _sut.Calculate(fills, equityCurve, 10_000m, Start, TwoYearsLater);

        // ~731 days (2024 is leap year: 366 + 365 = 731), not 1,000,000
        Assert.InRange(metrics.TradingDays, 730, 732);
    }

    [Fact]
    public void AnnualizedReturn_CorrectForMultiYear()
    {
        // $10,000 → $12,100 over 2 years = 10% CAGR
        // (12100/10000)^(1/2) - 1 = 0.10
        var equityCurve = new List<decimal> { 10_000m, 12_100m };
        var fills = new List<Fill>
        {
            TestFills.BuyAapl(100m, 10m),
            TestFills.SellAapl(121m, 10m)
        };

        var metrics = _sut.Calculate(fills, equityCurve, 10_000m, Start, TwoYearsLater);

        Assert.InRange(metrics.AnnualizedReturnPct, 9.5, 10.5);
    }

    [Fact]
    public void SharpeRatio_PositiveForProfitableMonotonicallyRisingEquity()
    {
        // Monotonically rising equity over 1 year → positive Sharpe
        var start = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var equityCurve = new List<decimal>();
        for (var i = 0; i <= 252; i++)
            equityCurve.Add(10_000m + i * 10m); // steady rise: 10000 → 12520

        var fills = new List<Fill>
        {
            TestFills.BuyAapl(100m, 100m),
            TestFills.SellAapl(125.2m, 100m)
        };

        var metrics = _sut.Calculate(fills, equityCurve, 10_000m, start, end);

        Assert.True(metrics.SharpeRatio > 0, $"Expected positive Sharpe, got {metrics.SharpeRatio}");
    }

    [Fact]
    public void SharpeRatio_ConsistentAcrossBarFrequencies()
    {
        // Same total return over same period, different bar counts (1-min vs 1-day resolution)
        var start = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

        // Daily bars: 252 points, linear growth 10000→12000
        var dailyCurve = new List<decimal>();
        for (var i = 0; i <= 252; i++)
            dailyCurve.Add(10_000m + i * (2_000m / 252));

        // Minute bars: 252*390 ≈ 98280 points, same linear growth
        var minuteBarCount = 252 * 390;
        var minuteCurve = new List<decimal>();
        for (var i = 0; i <= minuteBarCount; i++)
            minuteCurve.Add(10_000m + i * (2_000m / minuteBarCount));

        var fills = new List<Fill>
        {
            TestFills.BuyAapl(100m, 100m),
            TestFills.SellAapl(120m, 100m)
        };

        var dailyMetrics = _sut.Calculate(fills, dailyCurve, 10_000m, start, end);
        var minuteMetrics = _sut.Calculate(fills, minuteCurve, 10_000m, start, end);

        // Both must be positive — the old bug (hardcoded 252) made minute Sharpe deeply negative
        Assert.True(dailyMetrics.SharpeRatio > 0,
            $"Expected positive daily Sharpe, got {dailyMetrics.SharpeRatio}");
        Assert.True(minuteMetrics.SharpeRatio > 0,
            $"Expected positive minute Sharpe, got {minuteMetrics.SharpeRatio}");
    }

    [Fact]
    public void EmptyEquityCurve_ReturnsZeroMetrics()
    {
        var metrics = _sut.Calculate(
            new List<Fill>(), new List<decimal>(), 10_000m, Start, TwoYearsLater);

        Assert.Equal(0, metrics.TotalTrades);
        Assert.Equal(0.0, metrics.SharpeRatio);
        Assert.Equal(0.0, metrics.AnnualizedReturnPct);
        Assert.Equal(10_000m, metrics.InitialCapital);
    }

    [Fact]
    public void ZeroTimePeriod_ReturnsZeroAnnualized()
    {
        var sameTime = new DateTimeOffset(2024, 6, 15, 0, 0, 0, TimeSpan.Zero);
        var equityCurve = new List<decimal> { 10_000m, 11_000m };
        var fills = new List<Fill>
        {
            TestFills.BuyAapl(100m, 10m),
            TestFills.SellAapl(110m, 10m)
        };

        var metrics = _sut.Calculate(fills, equityCurve, 10_000m, sameTime, sameTime);

        Assert.Equal(0.0, metrics.AnnualizedReturnPct);
    }
}
