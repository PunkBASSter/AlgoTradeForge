using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Engine;

/// <summary>
/// Phase 2b — primary-sidecar binding on <see cref="BacktestFeedContext"/> (TRD §9.4).
/// <list type="bullet">
///   <item>P2b-11: a strategy that never calls <c>TryGetPrimarySidecar</c> triggers zero
///         loader invocations. The engine does no I/O for unused sidecars.</item>
///   <item>P2b-12: a registered sidecar whose loader returns <c>null</c> (broken manifest
///         pointer or missing on-disk data) surfaces a clear error at first access — not
///         silent <c>NaN</c> at runtime several bars later.</item>
/// </list>
/// </summary>
public sealed class BacktestFeedContextSidecarTests
{
    private static DataFeedSchema FlowSchema(string feedKey = "EqIV_ticks_500000.flow") =>
        new(feedKey, ["signed_imbalance", "buy_volume", "sell_volume", "realized_threshold"]);

    private static FeedSeries SidecarSeries() => new(
        [1000L, 2000L, 3000L],
        [
            [+0.5, -0.2, +0.1],   // signed_imbalance
            [1.0, 0.5, 0.7],      // buy_volume
            [0.5, 0.7, 0.6],      // sell_volume
            [0.5, 0.2, 0.1],      // realized_threshold
        ]);

    // ----- P2b-11 — sidecar zero-cost (lazy loader unused) -------------------

    [Fact]
    public void TryGetPrimarySidecar_WhenStrategyNeverAccesses_LoaderNotInvoked()
    {
        // Counter-based mock-equivalent: track invocations on the Func<FeedSeries?> closure.
        // No NSubstitute here — the closure is the simplest possible mock surface and keeps
        // the test self-contained.
        var loaderInvocations = 0;
        Func<FeedSeries?> loader = () => { loaderInvocations++; return SidecarSeries(); };

        var ctx = new BacktestFeedContext();
        ctx.RegisterPrimarySidecarLazy("EqIV_ticks_500000.flow", FlowSchema(), loader);

        // Run a typical engine cycle: AdvanceTo / Reset multiple times. Strategy doesn't query
        // the sidecar — exactly the "no flow data needed" path.
        ctx.AdvanceTo(1000);
        ctx.AdvanceTo(2000);
        ctx.AdvanceTo(3000);
        ctx.Reset();

        Assert.Equal(0, loaderInvocations);

        // Schema lookup is O(1) and does NOT trigger the loader — strategies resolve column
        // indices in OnInit without paying the I/O cost yet.
        IFeedContext face = ctx;
        Assert.NotNull(face.PrimarySidecarSchema);
        Assert.Equal(0, loaderInvocations);
    }

    [Fact]
    public void TryGetPrimarySidecar_FirstAccess_InvokesLoaderExactlyOnce()
    {
        var loaderInvocations = 0;
        Func<FeedSeries?> loader = () => { loaderInvocations++; return SidecarSeries(); };

        var ctx = new BacktestFeedContext();
        ctx.RegisterPrimarySidecarLazy("EqIV_ticks_500000.flow", FlowSchema(), loader);

        // Without AdvanceTo, the cursor is at 0 — no row materialized → returns false but
        // still invokes the loader to materialize the FeedSeries entry on first call.
        Assert.False(ctx.TryGetPrimarySidecar(out _));
        Assert.Equal(1, loaderInvocations);

        // Now advance past the first sidecar ts and re-query — loader still only invoked once.
        ctx.AdvanceTo(1000);
        Assert.True(ctx.TryGetPrimarySidecar(out var values));
        Assert.Equal(0.5, values[0]);
        Assert.Equal(1, loaderInvocations);
    }

    // ----- P2b-12 — binding correctness errors at engine init/first-access ----

    [Fact]
    public void TryGetPrimarySidecar_LoaderReturnsNull_ThrowsInvalidOperationException()
    {
        // Manifest declares a sidecar (via RegisterPrimarySidecarLazy) but the loader can't
        // find on-disk partitions. We surface this as an error at first sidecar access — far
        // better than silent NaN-everywhere in the strategy's bar handler.
        var ctx = new BacktestFeedContext();
        ctx.RegisterPrimarySidecarLazy(
            "EqIV_ticks_500000.flow", FlowSchema(),
            seriesLoader: () => null);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            ctx.TryGetPrimarySidecar(out _));

        Assert.Contains("EqIV_ticks_500000.flow", ex.Message);
        Assert.Contains("Re-aggregate", ex.Message);
    }

    [Fact]
    public void DefaultIFeedContext_TryGetPrimarySidecar_ReturnsFalse()
    {
        // Default-interface method: strategies running against a non-EqIV context (no sidecar
        // ever registered) get a clean false with no I/O. NullFeedContext exercises the same
        // default — pin both paths.
        IFeedContext null_ = NullFeedContext.Instance;
        Assert.False(null_.TryGetPrimarySidecar(out var v));
        Assert.True(v.IsEmpty);
        Assert.Null(null_.PrimarySidecarSchema);
        Assert.True(double.IsNaN(null_.GetPrimarySignedImbalance()));

        // BacktestFeedContext with no sidecar registered also falls through to the defaults.
        IFeedContext ctx = new BacktestFeedContext();
        Assert.False(ctx.TryGetPrimarySidecar(out _));
        Assert.Null(ctx.PrimarySidecarSchema);
    }

    [Fact]
    public void GetPrimarySignedImbalance_ResolvesColumnByName_ReturnsLatestValue()
    {
        // Convenience accessor's contract (TRD §9.4 default-method snippet): looks up
        // "signed_imbalance" in the schema's columns and returns that field's value from the
        // latest row buffer. Verify it works through the default interface dispatch.
        var loaderInvocations = 0;
        var ctx = new BacktestFeedContext();
        ctx.RegisterPrimarySidecarLazy(
            "EqIV_x.flow", FlowSchema(),
            seriesLoader: () => { loaderInvocations++; return SidecarSeries(); });

        IFeedContext face = ctx;
        Assert.True(double.IsNaN(face.GetPrimarySignedImbalance()));    // no row yet

        ctx.AdvanceTo(2000);
        Assert.Equal(-0.2, face.GetPrimarySignedImbalance());
        Assert.Equal(1, loaderInvocations);
    }
}
