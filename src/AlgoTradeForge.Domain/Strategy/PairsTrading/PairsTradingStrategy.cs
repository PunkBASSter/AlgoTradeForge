using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Indicators;
using AlgoTradeForge.Domain.Optimization.Attributes;
using AlgoTradeForge.Domain.Strategy.Modules;
using AlgoTradeForge.Domain.Strategy.Modules.CrossAsset;
using AlgoTradeForge.Domain.Strategy.Modules.TradeRegistry;
using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.Domain.Strategy.PairsTrading;

/// <summary>
/// Pairs trading strategy using z-score of the cross-asset spread.
/// Enters when z-score exceeds entry threshold, exits on reversion or cointegration break.
/// Executes on primary asset only — the secondary subscription provides the spread signal
/// but is not traded directly. True two-leg execution is a future enhancement.
/// </summary>
[StrategyKey("PairsTrading")]
public sealed class PairsTradingStrategy(
    PairsTradingParams parameters, IIndicatorFactory? indicators = null)
    : ModularStrategyBase<PairsTradingParams, PairsTradingContext>(parameters, indicators)
{
    public override string Version => "1.0.0";

    private CrossAssetModule _crossAsset = null!;
    private Atr _atr = null!;

    protected override void OnStrategyInit()
    {
        if (DataSubscriptions.Count < 2)
            throw new InvalidOperationException(
                "PairsTradingStrategy requires 2 data subscriptions (primary + secondary). " +
                "Provide an additional subscription via 'additionalSubscriptions'.");

        // ATR on primary subscription
        _atr = new Atr(Params.AtrPeriod);
        Indicators.Create(_atr, DataSubscriptions[0]);
        RegisterIndicator(_atr);

        // Cross-asset module
        _crossAsset = new CrossAssetModule(Params.CrossAsset);
        _crossAsset.Initialize(Indicators, DataSubscriptions[0], DataSubscriptions[1]);
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
                Context.Current = atrValues[^1];
        }
    }

    protected override void EvaluateEntry(Int64Bar bar, DataSubscription sub, IOrderContext orders)
    {
        if (!ReferenceEquals(Context.CurrentSubscription, DataSubscriptions[0]))
            return;

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

    protected override int GenerateSignal(Int64Bar bar, PairsTradingContext context)
    {
        if (context.ZScore == 0)
            return 0;

        int signal = 0;

        // Z-score > entry threshold → spread too wide → sell spread (sell A, buy B)
        if (context.ZScore > Params.CrossAsset.ZScoreEntryThreshold)
            signal = -80; // Sell
        // Z-score < -entry threshold → spread too narrow → buy spread (buy A, sell B)
        else if (context.ZScore < -Params.CrossAsset.ZScoreEntryThreshold)
            signal = 80;  // Buy

        return Math.Abs(signal) >= Params.SignalThreshold ? signal : 0;
    }

    protected override (long stopLoss, TpLevel[] takeProfits) GetRiskLevels(
        Int64Bar bar, OrderSide direction, long entryPrice, PairsTradingContext context)
    {
        var atr = context.Current;
        if (atr == 0) atr = bar.Close / 50;
        var distance = (long)(Params.AtrStopMultiplier * atr);

        var sl = direction == OrderSide.Buy
            ? (entryPrice != 0 ? entryPrice : bar.Close) - distance
            : (entryPrice != 0 ? entryPrice : bar.Close) + distance;

        return (sl, []);
    }

    protected override void ManagePositions(
        TradeRegistryModule tradeRegistry, PairsTradingContext context, IOrderContext orders)
    {
        if (!ReferenceEquals(context.CurrentSubscription, DataSubscriptions[0]))
            return;

        foreach (var group in tradeRegistry.ActiveGroups.ToArray())
        {
            var bar = context.CurrentBar;

            // Cointegration break → immediate exit
            if (!context.IsCointegrated)
            {
                tradeRegistry.LiquidateGroup(group.GroupId, orders);
                EmitSignal(bar.Timestamp, "Exit", context.CurrentSubscription.Asset.Name,
                    "Close", -100, "exit_score=-100 (cointegration break)");
                continue;
            }

            // Z-score reversion exit
            if (context.ZScore == 0) continue;

            var exitThreshold = Params.CrossAsset.ZScoreExitThreshold;
            var shouldExit =
                (group.EntrySide == OrderSide.Buy && context.ZScore > -exitThreshold) ||
                (group.EntrySide == OrderSide.Sell && context.ZScore < exitThreshold);

            if (shouldExit)
            {
                tradeRegistry.LiquidateGroup(group.GroupId, orders);
                EmitSignal(bar.Timestamp, "Exit", context.CurrentSubscription.Asset.Name,
                    "Close", -60, "exit_score=-60 (z-score reversion)");
            }
        }
    }

    protected override void CreateEntryGroup(
        Asset asset, OrderSide direction, OrderType orderType, long entryPrice,
        long stopLoss, TpLevel[] takeProfits, decimal quantity,
        PairsTradingContext context, IOrderContext orders)
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
