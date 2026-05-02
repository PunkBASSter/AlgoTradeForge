using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Indicators;
using AlgoTradeForge.Domain.Optimization.Attributes;
using AlgoTradeForge.Domain.Strategy.Modules;
using AlgoTradeForge.Domain.Strategy.Modules.TradeRegistry;
using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.Domain.Strategy.PrevBarBreakout;

/// <summary>
/// Symmetric previous-bar breakout reference strategy. Every bar's <c>OnBarComplete</c>:
/// <list type="bullet">
///   <item>Closes any active position at this bar's close (Limit @ bar.Close).</item>
///   <item>Cancels any unfilled pending entry from the previous bar.</item>
///   <item>Places a Buy-stop at <c>bar.High + EntryOffsetTicks</c> (SL = bar.Low - SlBufferTicks)
///         and a Sell-stop at <c>bar.Low - EntryOffsetTicks</c> (SL = bar.High + SlBufferTicks)
///         to evaluate against the <em>next</em> bar's price action.</item>
/// </list>
/// <para>
/// <c>EntryOffsetTicks</c> must be ≥ 1 for the breakout semantic to work — with offset 0
/// the stop sits exactly at the just-closed bar's H/L and the engine's post-OnBarComplete
/// same-bar fill loop would trigger it immediately. The strategy short-circuits placement
/// in that case.
/// </para>
/// Designed as a compute performance benchmark and reference for the <see cref="ModularStrategyBase{TParams,TContext}"/>
/// pipeline — exercises <c>TradeRegistry</c> and <c>MoneyManagement</c> under heavy
/// order flow, with optional volatility gating via the bundled <see cref="Atr"/>
/// indicator. <see cref="PrevBarBreakoutParams.MaxBars"/> controls hold length
/// measured from the entry's actual fill timestamp.
/// </summary>
[StrategyKey("PrevBarBreakout")]
public sealed class PrevBarBreakoutStrategy(
    PrevBarBreakoutParams parameters, IIndicatorFactory? indicators = null)
    : ModularStrategyBase<PrevBarBreakoutParams, PrevBarBreakoutContext>(parameters, indicators)
{
    public override string Version => "1.0.0";

    // Held as the wrapped indicator so the EmittingIndicatorDecorator's Compute is the one
    // the engine drives — that decorator is what emits the IndicatorEvent / IndicatorMutationEvent
    // pair the Debug UI consumes to render the ATR line. Holding the bare Atr would silently
    // bypass event emission (Buffers still update, but the chart stays empty).
    private IIndicator<Int64Bar, long> _atr = null!;
    private long _barIntervalMs;

    // GroupIds of the pending Buy/Sell pair placed in the previous bar's EvaluateEntry.
    // When one leg's entry fills, OnOrderFilled cancels the other leg so we never carry
    // two opposing positions. Both are cleared at the end of ManagePositions.
    private (long? Buy, long? Sell) _pending;

    protected override void OnStrategyInit()
    {
        _atr = Indicators.Create(new Atr(Params.AtrPeriod), DataSubscriptions[0]);
        RegisterIndicator(_atr);

        _barIntervalMs = (long)DataSubscriptions[0].TimeFrame.Duration.TotalMilliseconds;
    }

    protected override void OnContextUpdated(Int64Bar bar, DataSubscription sub)
    {
        var atrValues = _atr.Buffers["Value"];
        if (atrValues.Count > 0)
            Context.CurrentAtr = atrValues[^1];
    }

    protected override void OnOrderFilled(Fill fill, Order order)
    {
        // Runs inside the engine's first fill loop (before OnBarComplete), so anything we
        // cancel here takes effect on the same bar.
        if (order.GroupId is not { } gid) return;
        var group = TradeRegistry.GetGroup(gid);
        if (group is null || fill.OrderId != group.EntryOrderId) return;

        // Cancel the just-placed SL. TradeRegistry.HandleEntryFill submits an SL the instant
        // the entry fills; on a wide reversal bar that SL price can already be inside the
        // bar's range and would otherwise fire same-bar — pre-empting our intended
        // close-at-bar-close in ManagePositions. The group stays ProtectionActive (just
        // without an SL) until ManagePositions closes it.
        if (group.SlOrderId != 0)
            Orders.Cancel(group.SlOrderId);

        CancelOppositePendingLeg(gid);
    }

    private void CancelOppositePendingLeg(long filledGroupId)
    {
        if (filledGroupId == _pending.Buy && _pending.Sell is { } sellId)
        {
            TradeRegistry.CancelGroup(sellId);
            _pending.Sell = null;
        }
        else if (filledGroupId == _pending.Sell && _pending.Buy is { } buyId)
        {
            TradeRegistry.CancelGroup(buyId);
            _pending.Buy = null;
        }
    }

    protected override void ManagePositions(TradeRegistryModule tradeRegistry, PrevBarBreakoutContext context)
    {
        // ToArray because the close path mutates the active set via CancelGroup.
        foreach (var group in tradeRegistry.ActiveGroups.ToArray())
        {
            if (group.Status == OrderGroupStatus.ProtectionActive)
            {
                if (ShouldExitNow(group, context.CurrentBar))
                    CloseAtBarClose(tradeRegistry, group, context.CurrentBar);
            }
            else if (group.Status == OrderGroupStatus.PendingEntry)
            {
                tradeRegistry.CancelGroup(group.GroupId);
            }
        }

        // The previous bar's pending pair is now resolved (filled-and-maybe-closed,
        // expired-and-cancelled, or one filled and the other cancelled in OnOrderFilled).
        _pending = (null, null);
    }

    private bool ShouldExitNow(OrderGroup group, Int64Bar currentBar)
    {
        // Defensive: if we somehow see a ProtectionActive group with no fill timestamp,
        // exit immediately rather than holding a phantom position forever.
        if (group.EntryFilledAt is not { } filledAt)
            return true;

        var elapsedMs = currentBar.TimestampMs - filledAt.ToUnixTimeMilliseconds();
        var barsSinceFill = elapsedMs / _barIntervalMs;
        return barsSinceFill >= Params.MaxBars;
    }

    private void CloseAtBarClose(TradeRegistryModule tradeRegistry, OrderGroup group, Int64Bar bar)
    {
        var closeSide = group.EntrySide == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy;
        Orders.Submit(new Order
        {
            Id = 0, // engine assigns
            Asset = group.Asset,
            Side = closeSide,
            Type = OrderType.Limit,
            Quantity = group.RemainingQuantity,
            LimitPrice = bar.Close,
        });

        tradeRegistry.CancelGroup(group.GroupId);
    }

    private bool HasOpenPosition()
    {
        foreach (var existing in TradeRegistry.ActiveGroups)
        {
            if (existing.Status == OrderGroupStatus.ProtectionActive)
                return true;
        }
        return false;
    }

    protected override void EvaluateEntry(Int64Bar bar, DataSubscription sub)
    {
        // Hold-while-active: skip placing new pending pairs while a position from a previous
        // bar is still ProtectionActive. Keeps "one position at a time" absolute even when
        // MaxBars > 0 has the strategy sitting in a trade across multiple bars.
        if (HasOpenPosition()) return;

        // Breakout requires strictly above prior H / below prior L. With offset 0 the stop
        // would price exactly at bar.High / bar.Low and the engine's post-OnBarComplete
        // same-bar fill loop would trigger it immediately.
        if (Params.EntryOffsetTicks <= 0) return;

        // Volatility filter: skip entry when ATR is below the configured % of the just-closed
        // bar's close. 0 = disabled. ATR is unavailable until warmup completes; treat that as
        // "fail filter".
        if (Params.MinVolatilityPct > 0.0)
        {
            if (Context.CurrentAtr <= 0 || bar.Close <= 0)
                return;

            var volatilityPct = (double)Context.CurrentAtr / bar.Close * 100.0;
            if (volatilityPct < Params.MinVolatilityPct)
                return;
        }

        var asset = sub.Asset;

        // Buy-stop @ bar.High + offset, SL = bar.Low - buffer.
        var buyEntry = bar.High + Params.EntryOffsetTicks;
        var buySl = bar.Low - Params.SlBufferTicks;
        if (buyEntry > buySl)
            _pending.Buy = TryPlaceBreakoutStop(asset, OrderSide.Buy, buyEntry, buySl);

        // Sell-stop @ bar.Low - offset, SL = bar.High + buffer.
        var sellEntry = bar.Low - Params.EntryOffsetTicks;
        var sellSl = bar.High + Params.SlBufferTicks;
        if (sellEntry > 0 && sellEntry < sellSl)
            _pending.Sell = TryPlaceBreakoutStop(asset, OrderSide.Sell, sellEntry, sellSl);
    }

    private long? TryPlaceBreakoutStop(Asset asset, OrderSide side, long entry, long sl)
    {
        var qty = Params.MoneyManagement.CalculateSize(entry, sl, Context, asset);
        if (qty < asset.MinOrderQuantity) return null;

        var group = TradeRegistry.OpenGroup(
            asset, side, OrderType.Stop, qty,
            slPrice: sl, tpLevels: ReadOnlySpan<TpLevel>.Empty,
            entryStopPrice: entry);
        return group?.GroupId;
    }
}
