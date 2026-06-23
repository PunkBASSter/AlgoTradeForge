using AlgoTradeForge.Domain.Strategy.Subscriptions;
using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.Modules.MoneyManagement;
using AlgoTradeForge.Domain.Strategy.Modules.TradeRegistry;
using AlgoTradeForge.Domain.Strategy.PrevBarBreakout;
using AlgoTradeForge.Domain.Tests.TestUtilities;
using AlgoTradeForge.Domain.Trading;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Strategy;

/// <summary>
/// End-to-end integration tests that drive <see cref="PrevBarBreakoutStrategy"/> through
/// a deterministic synthetic bar sequence and assert on the resulting fill timeline.
/// Replaces ad-hoc inspection of debug event logs with executable, regression-proof checks.
/// </summary>
public sealed class PrevBarBreakoutStrategyTests
{
    private static readonly BacktestEngine Engine = new(new BarMatcher(), new OrderValidator());

    private static BacktestOptions CreateOptions(long initialCash = 10_000_000_000L) => new()
    {
        InitialCash = initialCash,
        StartTime = DateTimeOffset.MinValue,
        EndTime = DateTimeOffset.MaxValue,
    };

    private static PrevBarBreakoutParams CreateParams(long entryOffsetTicks = 5, long slBufferTicks = 5, int maxBars = 0) => new()
    {
        EntryOffsetTicks = entryOffsetTicks,
        SlBufferTicks = slBufferTicks,
        MaxBars = maxBars,
        AtrPeriod = 4,
        MinVolatilityPct = 0.0,
        MoneyManagement = new FixedNotionalModule(new FixedNotionalParams { Notional = 1000_000 }),
        TradeRegistry = new TradeRegistryParams { MaxConcurrentGroups = 2 },
        DataSubscriptions = [TestSubs.Of(TestAssets.BtcUsdt, new TimeFrame(TimeSpan.FromMinutes(1)))],
    };

    [Fact]
    public void OffsetZero_PlacesNoPendings_GuardBlocksSameBarFill()
    {
        // With EntryOffsetTicks = 0 the stop sits exactly at bar.High / bar.Low and the
        // engine's post-OnBarComplete same-bar fill loop would trigger it immediately.
        // The strategy guards against this — verifying no fills happen at all.
        var bars = TestBars.CreateSeries(
            TestBars.Create(10000, 11000, 9000, 10500),
            TestBars.Create(10500, 12000, 10000, 11500),
            TestBars.Create(11500, 13000, 10500, 12500));

        var strategy = new PrevBarBreakoutStrategy(CreateParams(entryOffsetTicks: 0, slBufferTicks: 0));
        var result = Engine.Run([bars], strategy, CreateOptions(), TestContext.Current.CancellationToken);

        Assert.Empty(result.Fills);
    }

    [Fact]
    public void NewPendingsDoNotFillSameBar_OffByOneAndSameBarBugsFixed()
    {
        // The crux of the recent bug: pendings placed in OnBarComplete must NOT fill in
        // the same bar's post-OnBarComplete fill loop. Constructed so that the bar that
        // fills the prior pending also has range that, with the buggy strategy, would
        // have triggered the just-placed fresh pendings same-bar.
        //
        // Bar 0 (10000/11000/9000/10500) — no prior position; OnBarComplete places
        //   Buy stop @ 11000+5=11005, Sell stop @ 9000-5=8995. Same-bar guard ok (offset>0).
        //
        // Bar 1 (10500/12000/10000/11500) — Buy stop @ 11005 fills (bar.High=12000 ≥ 11005)
        //   at price 11005. Sell stop @ 8995 cancelled by OnOrderFilled (bar.Low=10000 > 8995
        //   wouldn't have triggered anyway, but cancel is unconditional on fill of the pair).
        //   ManagePositions closes at bar.Close=11500 via Sell-Limit; same-bar fill loop
        //   fills it. EvaluateEntry places NEW pair: Buy stop @ 12000+5=12005, Sell stop @
        //   10000-5=9995. Same-bar fill loop also processes these — bar.High=12000 < 12005,
        //   bar.Low=10000 > 9995, so neither triggers (bug fixed). 2 fills on bar 1.
        //
        // Bar 2 (11500/13000/10500/12500) — Buy stop @ 12005 fills (bar.High=13000 ≥ 12005)
        //   at 12005. Closed at bar.Close=12500 via Sell-Limit. 2 more fills.

        var bars = TestBars.CreateSeries(
            TestBars.Create(10000, 11000, 9000, 10500),
            TestBars.Create(10500, 12000, 10000, 11500),
            TestBars.Create(11500, 13000, 10500, 12500));

        var strategy = new PrevBarBreakoutStrategy(CreateParams());
        var result = Engine.Run([bars], strategy, CreateOptions(), TestContext.Current.CancellationToken);

        // 4 fills total: 2 entries (Buy stops) + 2 closes (Sell limits at bar.Close). No SL fires.
        Assert.Equal(4, result.Fills.Count);

        // Bar 1: Buy entry @ 11005, Limit close @ bar.Close=11500
        Assert.Equal(11005, result.Fills[0].Price);
        Assert.Equal(OrderSide.Buy, result.Fills[0].Side);
        Assert.Equal(11500, result.Fills[1].Price);
        Assert.Equal(OrderSide.Sell, result.Fills[1].Side);

        // Bar 2: Buy entry @ 12005, Limit close @ bar.Close=12500
        Assert.Equal(12005, result.Fills[2].Price);
        Assert.Equal(OrderSide.Buy, result.Fills[2].Side);
        Assert.Equal(12500, result.Fills[3].Price);
        Assert.Equal(OrderSide.Sell, result.Fills[3].Side);
    }

    [Fact]
    public void OppositeLeg_CancelledOnEntryFill_NeverFillsSameBar()
    {
        // Both entries have prices that the next bar's range covers. The strategy must
        // cancel the opposite leg the moment one fills, so we never carry two opposing
        // positions even on a "wide" bar.
        //
        // Bar 0 (10000/10500/9500/10000): pendings placed at 10500+5=10505 (Buy) and
        //   9500-5=9495 (Sell).
        // Bar 1 (10000/11000/9000/10500): wide bar that covers both stops. First-fill-loop
        //   fills the Buy at 10505 (bar.High=11000 ≥ 10505). OnOrderFilled cancels the
        //   Sell pending — even though bar.Low=9000 ≤ 9495 would have triggered it.
        var bars = TestBars.CreateSeries(
            TestBars.Create(10000, 10500, 9500, 10000),
            TestBars.Create(10000, 11000, 9000, 10500));

        var strategy = new PrevBarBreakoutStrategy(CreateParams());
        var result = Engine.Run([bars], strategy, CreateOptions(), TestContext.Current.CancellationToken);

        // Exactly 2 fills: one Buy entry, one Sell-Limit close at bar.Close. Sell entry
        // never fired because OnOrderFilled cancelled it before the loop reached it.
        Assert.Equal(2, result.Fills.Count);
        Assert.Equal(OrderSide.Buy, result.Fills[0].Side);
        Assert.Equal(10505, result.Fills[0].Price);
        Assert.Equal(OrderSide.Sell, result.Fills[1].Side);
        Assert.Equal(10500, result.Fills[1].Price); // bar.Close
    }

    [Fact]
    public void MaxBars1_HoldsOneBarBeyondFill_BeforeClosing()
    {
        // MaxBars = 1 means "exit after barsSinceFill >= 1" (i.e. on the bar AFTER the fill).
        // Bar 0: placement.
        // Bar 1: Buy entry fills @ 10505, barsSinceFill=0 → ShouldExitNow=false → hold.
        //        EvaluateEntry skips (ProtectionActive).
        // Bar 2: barsSinceFill=1 → ShouldExitNow=true → close at bar 2's close.
        var bars = TestBars.CreateSeries(
            TestBars.Create(10000, 10500, 9500, 10000),
            TestBars.Create(10000, 11000, 10100, 10800),
            TestBars.Create(10800, 11200, 10500, 11000));

        var strategy = new PrevBarBreakoutStrategy(CreateParams(maxBars: 1));
        var result = Engine.Run([bars], strategy, CreateOptions(), TestContext.Current.CancellationToken);

        // Buy entry on bar 1, Limit close on bar 2 at bar 2's close.
        Assert.Equal(2, result.Fills.Count);
        Assert.Equal(OrderSide.Buy, result.Fills[0].Side);
        Assert.Equal(10505, result.Fills[0].Price);
        Assert.Equal(OrderSide.Sell, result.Fills[1].Side);
        Assert.Equal(11000, result.Fills[1].Price); // bar 2's close, not bar 1's
    }

    [Fact]
    public void EntryFillCancelsSL_BeforeSameBarFillLoopReachesIt()
    {
        // Engine-ordering invariant: OnOrderFilled MUST run inside the engine's first fill
        // loop (before OnBarComplete) so the SL we cancel is gone before the same-bar fill
        // loop can trigger it. If a future engine change ever flips that ordering, this test
        // fails: the SL would fire on the same bar and result in an extra fill at the SL
        // price instead of the intended Limit-close at bar.Close.
        //
        // Bar 0 (10000/10500/9500/10000): pendings placed at 10500+5=10505 (Buy) and
        //   9500-5=9495 (Sell). Buy SL = bar.Low - SlBufferTicks = 9500-5 = 9495.
        // Bar 1 (10000/12000/9000/11500): wide reversal bar.
        //   - bar.High=12000 ≥ 10505 → Buy entry fills @ 10505.
        //   - HandleEntryFill submits the Buy SL @ 9495.
        //   - bar.Low=9000 ≤ 9495 — if the SL survived, the same-bar loop would fire it.
        //   - OnOrderFilled cancels the SL before that loop reaches it.
        //   - ManagePositions issues Limit-close @ bar.Close=11500.
        //   - Expected: Buy entry @ 10505, Sell-Limit close @ 11500. NO SL fill.
        var bars = TestBars.CreateSeries(
            TestBars.Create(10000, 10500, 9500, 10000),
            TestBars.Create(10000, 12000, 9000, 11500));

        var strategy = new PrevBarBreakoutStrategy(CreateParams());
        var result = Engine.Run([bars], strategy, CreateOptions(), TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Fills.Count);
        Assert.Equal(OrderSide.Buy, result.Fills[0].Side);
        Assert.Equal(10505, result.Fills[0].Price);
        Assert.Equal(OrderSide.Sell, result.Fills[1].Side);
        // CRITICAL: close at bar.Close, NOT at the SL price (9495). If this assertion ever
        // fails with 9495, the engine has stopped running OnOrderFilled before the same-bar
        // fill loop and the strategy's SL-cancel optimization is broken.
        Assert.Equal(11500, result.Fills[1].Price);
    }

    [Fact]
    public void NoBreakout_NoFills()
    {
        // Quiet bars whose range never exceeds prior bar's range — no entries fire.
        var bars = TestBars.CreateSeries(
            TestBars.Create(10000, 10500, 9500, 10000),
            TestBars.Create(10000, 10300, 9700, 10000),
            TestBars.Create(10000, 10300, 9700, 10000));

        var strategy = new PrevBarBreakoutStrategy(CreateParams());
        var result = Engine.Run([bars], strategy, CreateOptions(), TestContext.Current.CancellationToken);

        Assert.Empty(result.Fills);
    }
}
