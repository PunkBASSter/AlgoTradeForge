using AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;
using Xunit;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live.InteractiveBrokers;

public class FuturesFrontMonthSelectorTests
{
    private static IbContractDetailsResult C(int conId, string expiry) => new(conId, $"GC{conId}", expiry);

    [Fact]
    public void SelectFrontMonth_PicksNearestNonExpired()
    {
        var today = new DateOnly(2026, 6, 26);
        var candidates = new[] { C(3, "20270226"), C(1, "20261229"), C(2, "20270127") };

        var chosen = FuturesFrontMonthSelector.SelectFrontMonth(candidates, today);

        Assert.Equal(1, chosen.ConId);
        Assert.Equal("20261229", chosen.LastTradeDate);
    }

    [Fact]
    public void SelectFrontMonth_SkipsExpired()
    {
        var today = new DateOnly(2026, 6, 26);
        var candidates = new[] { C(1, "20260130"), C(2, "20260828") };

        var chosen = FuturesFrontMonthSelector.SelectFrontMonth(candidates, today);

        Assert.Equal(2, chosen.ConId);
    }

    [Fact]
    public void SelectFrontMonth_AcceptsYearMonthFormat()
    {
        var today = new DateOnly(2026, 6, 26);
        var candidates = new[] { C(1, "202612"), C(2, "202703") };

        var chosen = FuturesFrontMonthSelector.SelectFrontMonth(candidates, today);

        Assert.Equal(1, chosen.ConId);
    }

    [Fact]
    public void SelectFrontMonth_AllExpired_Throws()
    {
        var today = new DateOnly(2026, 6, 26);
        var candidates = new[] { C(1, "20260130") };
        Assert.Throws<InvalidOperationException>(() => FuturesFrontMonthSelector.SelectFrontMonth(candidates, today));
    }

    [Fact]
    public void SelectFrontMonth_Empty_Throws() =>
        Assert.Throws<InvalidOperationException>(() =>
            FuturesFrontMonthSelector.SelectFrontMonth([], new DateOnly(2026, 6, 26)));
}
