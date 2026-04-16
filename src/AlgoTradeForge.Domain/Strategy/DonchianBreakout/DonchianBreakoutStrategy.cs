using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Indicators;
using AlgoTradeForge.Domain.Optimization.Attributes;
using AlgoTradeForge.Domain.Strategy.Modules;
using AlgoTradeForge.Domain.Strategy.Modules.Exit;
using AlgoTradeForge.Domain.Strategy.Modules.Regime;
using AlgoTradeForge.Domain.Strategy.Modules.TradeRegistry;
using AlgoTradeForge.Domain.Strategy.Modules.TrailingStop;
using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.Domain.Strategy.DonchianBreakout;

/// <summary>
/// Donchian Channel Breakout strategy — enters on channel breakouts with stop orders,
/// uses trailing stops, regime detection, and regime filtering.
/// </summary>
[StrategyKey("DonchianBreakout")]
public sealed class DonchianBreakoutStrategy(
    DonchianParams parameters, IIndicatorFactory? indicators = null)
    : ModularStrategyBase<DonchianParams, DonchianContext>(parameters, indicators)
{
    public override string Version => "1.0.0";

    private DonchianChannel _entryChannel = null!;
    private DonchianChannel _exitChannel = null!;
    private Atr _atr = null!;
    private TrailingStopModule _trailingStopModule = null!;
    private RegimeDetectorModule _regimeDetector = null!;
    private RegimeChangeExitRule _regimeChangeExit = null!;
    private TimeBasedExitRule? _timeBasedExit;

    protected override void OnStrategyInit()
    {
        // Create indicators
        _entryChannel = new DonchianChannel(Params.EntryPeriod);
        Indicators.Create(_entryChannel, DataSubscriptions[0]);
        RegisterIndicator(_entryChannel);

        _exitChannel = new DonchianChannel(Params.ExitPeriod);
        Indicators.Create(_exitChannel, DataSubscriptions[0]);
        RegisterIndicator(_exitChannel);

        _atr = new Atr(Params.AtrPeriod);
        Indicators.Create(_atr, DataSubscriptions[0]);
        RegisterIndicator(_atr);

        // Trailing stop
        _trailingStopModule = new TrailingStopModule(Params.TrailingStopConfig);

        // Regime detector
        _regimeDetector = new RegimeDetectorModule(Params.RegimeDetectorConfig);
        _regimeDetector.Initialize(Indicators, DataSubscriptions[0]);

        // Exit rules
        _regimeChangeExit = new RegimeChangeExitRule(Context);

        if (Params.Exit is { MaxHoldBars: > 0 } exitParams)
        {
            var intervalMs = (long)DataSubscriptions[0].TimeFrame.TotalMilliseconds;
            _timeBasedExit = new TimeBasedExitRule(exitParams.MaxHoldBars, intervalMs);
        }
    }

    protected override void OnContextUpdated(Int64Bar bar, DataSubscription sub)
    {
        var atrValues = _atr.Buffers["Value"];
        if (atrValues.Count > 0)
            Context.Current = atrValues[^1];

        // Update regime detector (previously handled by base)
        _regimeDetector.Update(bar, Context);
    }

    protected override void EvaluateEntry(Int64Bar bar, DataSubscription sub, IOrderContext orders)
    {
        var signalStrength = GenerateSignal(bar, Context);
        if (signalStrength == 0)
            return;

        var direction = signalStrength > 0 ? OrderSide.Buy : OrderSide.Sell;

        var (entryPrice, orderType) = GetEntryPrice(bar, direction, Context);
        var (stopLoss, takeProfits) = GetRiskLevels(bar, direction, entryPrice, Context);

        if (entryPrice != 0)
        {
            if (direction == OrderSide.Buy && stopLoss >= entryPrice) return;
            if (direction == OrderSide.Sell && stopLoss <= entryPrice) return;
        }
        else
        {
            if (direction == OrderSide.Buy && stopLoss >= bar.Close) return;
            if (direction == OrderSide.Sell && stopLoss <= bar.Close) return;
        }

        var quantity = Params.MoneyManagement.CalculateSize(
            entryPrice != 0 ? entryPrice : bar.Close, stopLoss, Context, sub.Asset);
        if (quantity < sub.Asset.MinOrderQuantity)
            return;

        CreateEntryGroup(sub.Asset, direction, orderType, entryPrice,
            stopLoss, takeProfits, quantity, Context, orders);

        EmitSignal(bar.Timestamp, "Entry", sub.Asset.Name,
            direction.ToString(), signalStrength,
            $"type={orderType}, sl={stopLoss}, qty={quantity}");
    }

    protected override int GenerateSignal(Int64Bar bar, DonchianContext context)
    {
        // Regime filter: block when explicitly non-trending
        if (context.CurrentRegime == MarketRegime.RangeBound)
            return 0;

        var upper = _entryChannel.Buffers["Upper"];
        var lower = _entryChannel.Buffers["Lower"];
        if (upper.Count < 2) return 0;

        var prevUpper = upper[^2];
        var prevLower = lower[^2];
        if (prevUpper == 0 || prevLower == 0) return 0;

        int signal = 0;

        // Breakout above previous bar's upper channel
        if (bar.High > prevUpper)
            signal = 80;  // Buy
        // Breakout below previous bar's lower channel
        else if (bar.Low < prevLower)
            signal = -80; // Sell

        return Math.Abs(signal) >= Params.SignalThreshold ? signal : 0;
    }

    protected override (long price, OrderType type) GetEntryPrice(
        Int64Bar bar, OrderSide direction, DonchianContext context)
    {
        var upper = _entryChannel.Buffers["Upper"];
        var lower = _entryChannel.Buffers["Lower"];
        if (upper.Count < 2) return (0, OrderType.Market);

        // Stop order at the previous bar's channel boundary
        var price = direction == OrderSide.Buy ? upper[^2] : lower[^2];
        return (price, OrderType.Stop);
    }

    protected override (long stopLoss, TpLevel[] takeProfits) GetRiskLevels(
        Int64Bar bar, OrderSide direction, long entryPrice, DonchianContext context)
    {
        var atr = context.Current;
        if (atr == 0) atr = bar.Close / 50;

        var distance = (long)(Params.AtrStopMultiplier * atr);
        var sl = direction == OrderSide.Buy
            ? entryPrice - distance
            : entryPrice + distance;

        return (sl, []);
    }

    protected override void ManagePositions(
        TradeRegistryModule tradeRegistry, DonchianContext context, IOrderContext orders)
    {
        foreach (var group in tradeRegistry.ActiveGroups.ToArray())
        {
            var bar = context.CurrentBar;

            // Trailing stop adjustment
            var atr = context.Current;
            var newStop = _trailingStopModule.Update(group.GroupId, bar, atr);

            // Exit evaluation
            var exitSignal = _regimeChangeExit.Evaluate(bar, context, group);
            if (_timeBasedExit is not null)
                exitSignal = Math.Min(exitSignal, _timeBasedExit.Evaluate(bar, context, group));

            if (exitSignal <= Params.ExitThreshold)
            {
                tradeRegistry.LiquidateGroup(group.GroupId, orders);
                _trailingStopModule.Remove(group.GroupId);
                EmitSignal(bar.Timestamp, "Exit", context.CurrentSubscription.Asset.Name,
                    "Close", exitSignal, $"exit_score={exitSignal}");
            }
            else if (newStop is not null && newStop.Value != group.SlPrice)
            {
                tradeRegistry.UpdateStopLoss(group.GroupId, newStop.Value, orders);
            }
        }
    }

    protected override void OnOrderFilled(Fill fill, Order order)
    {
        var registry = ((ITradeRegistryProvider)this).TradeRegistry;

        // Activate trailing stop on entry fill
        foreach (var group in registry.ActiveGroups)
        {
            if (group.EntryOrderId == order.Id && group.Status == OrderGroupStatus.ProtectionActive)
            {
                _trailingStopModule.Activate(
                    group.GroupId,
                    group.EntryPrice,
                    group.EntrySide,
                    group.SlPrice);

                // Record entry regime for regime-change exit rule
                _regimeChangeExit.Activate(group.GroupId, Context.CurrentRegime);
                break;
            }
        }

        // Clean up stale regime-change tracking for closed groups
        _regimeChangeExit.RemoveInactive(registry.ActiveGroups);
    }
}
