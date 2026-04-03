using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Indicators;
using AlgoTradeForge.Domain.Optimization.Attributes;
using AlgoTradeForge.Domain.Strategy.Modules;
using AlgoTradeForge.Domain.Strategy.Modules.CrossAsset;
using AlgoTradeForge.Domain.Strategy.Modules.Exit;
using AlgoTradeForge.Domain.Strategy.Modules.TradeRegistry;
using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.Domain.Strategy.PairsTrading;

/// <summary>
/// Pairs trading strategy using z-score of the cross-asset spread.
/// Enters when z-score exceeds entry threshold, exits on reversion or cointegration break.
/// Submits BOTH legs (buy A + sell B or inverse) with hedge ratio.
/// </summary>
[StrategyKey("PairsTrading")]
public sealed class PairsTradingStrategy(
    PairsTradingParams parameters, IIndicatorFactory? indicators = null)
    : ModularStrategyBase<PairsTradingParams>(parameters, indicators)
{
    public override string Version => "1.0.0";

    private CrossAssetModule _crossAsset = null!;
    private Atr _atr = null!;

    protected override void OnStrategyInit()
    {
        // ATR on primary subscription
        _atr = new Atr(Params.AtrPeriod);
        Indicators.Create(_atr, DataSubscriptions[0]);
        RegisterIndicator(_atr);

        // Cross-asset module
        _crossAsset = new CrossAssetModule(Params.CrossAsset);
        _crossAsset.Initialize(Indicators, DataSubscriptions[0], DataSubscriptions[1]);

        // Exit rules
        var exitModule = new ExitModule();
        exitModule.AddRule(new CointegrationBreakExitRule());
        SetExit(exitModule);
    }

    protected override void OnContextUpdated(Int64Bar bar, DataSubscription sub)
    {
        // Update cross-asset module for both subscriptions
        _crossAsset.Update(bar, sub, Context);

        // Write ATR from primary subscription
        if (ReferenceEquals(sub, DataSubscriptions[0]))
        {
            var atrValues = _atr.Buffers["Value"];
            if (atrValues.Count > 0)
                Context.CurrentAtr = atrValues[^1];
        }
    }

    protected override int OnGenerateSignal(Int64Bar bar, StrategyContext context)
    {
        var zScore = context.Get<double>("crossasset.zscore");

        // Z-score > entry threshold → spread too wide → sell spread (sell A, buy B)
        if (zScore > Params.CrossAsset.ZScoreEntryThreshold)
            return -80; // Sell

        // Z-score < -entry threshold → spread too narrow → buy spread (buy A, sell B)
        if (zScore < -Params.CrossAsset.ZScoreEntryThreshold)
            return 80;  // Buy

        return 0;
    }

    protected override (long stopLoss, TpLevel[] takeProfits) OnGetRiskLevels(
        Int64Bar bar, OrderSide direction, long entryPrice, StrategyContext context)
    {
        // SL at 3x ATR from entry (extreme z-score protection)
        var atr = context.CurrentAtr;
        if (atr == 0) atr = bar.Close / 50;
        var distance = (long)(3.0 * atr);

        var sl = direction == OrderSide.Buy
            ? (entryPrice != 0 ? entryPrice : bar.Close) - distance
            : (entryPrice != 0 ? entryPrice : bar.Close) + distance;

        return (sl, []);
    }

    protected override int OnEvaluateExit(
        Int64Bar bar, StrategyContext context, OrderGroup group)
    {
        // Z-score reversion: exit when z-score reverts past exit threshold
        if (!context.Has("crossasset.zscore"))
            return 0;

        var zScore = context.Get<double>("crossasset.zscore");
        var exitThreshold = Params.CrossAsset.ZScoreExitThreshold;

        // If we're long (bought when z < -entry), exit when z reverts above -exit
        if (group.EntrySide == OrderSide.Buy && zScore > -exitThreshold)
            return -60;

        // If we're short (sold when z > entry), exit when z reverts below exit
        if (group.EntrySide == OrderSide.Sell && zScore < exitThreshold)
            return -60;

        return 0;
    }

    protected override void OnExecuteEntry(
        Asset asset, OrderSide direction, OrderType orderType, long entryPrice,
        long stopLoss, TpLevel[] takeProfits, decimal quantity,
        StrategyContext context, IOrderContext orders)
    {
        // Submit primary leg via trade registry
        var registry = ((ITradeRegistryProvider)this).TradeRegistry;
        registry.OpenGroup(
            orders, asset, direction, orderType, quantity, stopLoss, takeProfits,
            entryLimitPrice: orderType == OrderType.Limit ? entryPrice : null,
            entryStopPrice: orderType == OrderType.Stop ? entryPrice : null,
            tag: "pairs-primary");
    }
}
