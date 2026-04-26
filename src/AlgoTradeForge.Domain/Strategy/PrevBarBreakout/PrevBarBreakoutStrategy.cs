using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Indicators;
using AlgoTradeForge.Domain.Optimization.Attributes;
using AlgoTradeForge.Domain.Strategy.Modules;
using AlgoTradeForge.Domain.Strategy.Modules.MaxHoldBars;
using AlgoTradeForge.Domain.Strategy.Modules.TradeRegistry;
using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.Domain.Strategy.PrevBarBreakout;

/// <summary>
/// Symmetric previous-bar breakout reference strategy. Every bar:
/// <list type="bullet">
///   <item>Liquidates any active position at this bar's close (MOC).</item>
///   <item>Cancels any unfilled pending entry from the previous bar.</item>
///   <item>Places a Buy-stop above the previous bar's high (SL = prev.Low - buffer)
///         and a Sell-stop below the previous bar's low (SL = prev.High + buffer),
///         gated by the ATR / prev.Close volatility filter.</item>
/// </list>
/// Designed as a compute performance benchmark and reference for the <see cref="ModularStrategyBase{TParams,TContext}"/>
/// pipeline — exercises <c>TradeRegistry</c>, <c>MoneyManagement</c>, and
/// <c>MaxHoldBars</c> under heavy order flow, with optional volatility gating
/// via the bundled <see cref="Atr"/> indicator.
/// </summary>
[StrategyKey("PrevBarBreakout")]
public sealed class PrevBarBreakoutStrategy(
    PrevBarBreakoutParams parameters, IIndicatorFactory? indicators = null)
    : ModularStrategyBase<PrevBarBreakoutParams, PrevBarBreakoutContext>(parameters, indicators)
{
    public override string Version => "1.0.0";

    private Atr _atr = null!;
    private MaxHoldBarsModule _maxHoldBars = null!;
    private long _barIntervalMs;
    private bool _hasPrevBar;
    private Int64Bar _prevBar;

    protected override void OnStrategyInit()
    {
        _atr = new Atr(Params.AtrPeriod);
        Indicators.Create(_atr, DataSubscriptions[0]);
        RegisterIndicator(_atr);

        _maxHoldBars = new MaxHoldBarsModule(new MaxHoldBarsParams
        {
            Enabled = true,
            MaxBars = Params.MaxBars,
        });
        _barIntervalMs = (long)DataSubscriptions[0].TimeFrame.TotalMilliseconds;
    }

    protected override void OnContextUpdated(Int64Bar bar, DataSubscription sub)
    {
        var atrValues = _atr.Buffers["Value"];
        if (atrValues.Count > 0)
            Context.CurrentAtr = atrValues[^1];
    }

    protected override void ManagePositions(TradeRegistryModule tradeRegistry, PrevBarBreakoutContext context)
    {
        // Single iteration over active groups: liquidate fills, cancel leftover pendings.
        // ToArray is intentional because LiquidateGroup/CancelGroup mutates the active set.
        foreach (var group in tradeRegistry.ActiveGroups.ToArray())
        {
            if (group.Status == OrderGroupStatus.ProtectionActive)
            {
                _ = _maxHoldBars.ShouldClose(context.CurrentBar.TimestampMs, group.CreatedAt, _barIntervalMs);
                tradeRegistry.LiquidateGroup(group.GroupId);
            }
            else if (group.Status == OrderGroupStatus.PendingEntry)
            {
                tradeRegistry.CancelGroup(group.GroupId);
            }
        }
    }

    protected override void EvaluateEntry(Int64Bar bar, DataSubscription sub)
    {
        if (!_hasPrevBar)
        {
            _prevBar = bar;
            _hasPrevBar = true;
            return;
        }

        var prev = _prevBar;
        _prevBar = bar;

        // Volatility filter: skip entry when ATR is below the configured % of prev close.
        // 0 = disabled. ATR is unavailable until warmup completes; treat that as "fail filter".
        if (Params.MinVolatilityPct > 0.0)
        {
            if (Context.CurrentAtr <= 0 || prev.Close <= 0)
                return;

            var volatilityPct = (double)Context.CurrentAtr / prev.Close * 100.0;
            if (volatilityPct < Params.MinVolatilityPct)
                return;
        }

        var asset = sub.Asset;

        // Buy-stop @ prev.High + offset, SL = prev.Low - buffer
        var buyEntry = prev.High + Params.EntryOffsetTicks;
        var buySl = prev.Low - Params.SlBufferTicks;
        if (buyEntry > buySl)
        {
            var qty = Params.MoneyManagement.CalculateSize(buyEntry, buySl, Context, asset);
            if (qty >= asset.MinOrderQuantity)
            {
                TradeRegistry.OpenGroup(
                    asset, OrderSide.Buy, OrderType.Stop, qty,
                    slPrice: buySl, tpLevels: ReadOnlySpan<TpLevel>.Empty,
                    entryStopPrice: buyEntry);
            }
        }

        // Sell-stop @ prev.Low - offset, SL = prev.High + buffer
        var sellEntry = prev.Low - Params.EntryOffsetTicks;
        var sellSl = prev.High + Params.SlBufferTicks;
        if (sellEntry < sellSl && sellEntry > 0)
        {
            var qty = Params.MoneyManagement.CalculateSize(sellEntry, sellSl, Context, asset);
            if (qty >= asset.MinOrderQuantity)
            {
                TradeRegistry.OpenGroup(
                    asset, OrderSide.Sell, OrderType.Stop, qty,
                    slPrice: sellSl, tpLevels: ReadOnlySpan<TpLevel>.Empty,
                    entryStopPrice: sellEntry);
            }
        }
    }
}
