using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Indicators;
using AlgoTradeForge.Domain.Optimization.Attributes;
using AlgoTradeForge.Domain.Strategy.Modules;
using AlgoTradeForge.Domain.Strategy.Modules.TradeRegistry;
using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.Domain.Strategy.Rsi2MeanReversion;

[StrategyKey("RSI2-MeanReversion")]
public sealed class Rsi2MeanReversionStrategy(
    Rsi2Params parameters, IIndicatorFactory? indicators = null)
    : ModularStrategyBase<Rsi2Params, Rsi2Context>(parameters, indicators)
{
    public override string Version => "1.0.0";

    private Rsi _rsi = null!;
    private Sma _trendFilter = null!;
    private Atr _atr = null!;
    private Atr _filterAtr = null!;

    protected override void OnStrategyInit()
    {
        _rsi = new Rsi(Params.RsiPeriod);
        Indicators.Create(_rsi, DataSubscriptions[0]);
        RegisterIndicator(_rsi);

        _trendFilter = new Sma(Params.TrendFilterPeriod);
        Indicators.Create(_trendFilter, DataSubscriptions[0]);
        RegisterIndicator(_trendFilter);

        _atr = new Atr(Params.AtrPeriod);
        Indicators.Create(_atr, DataSubscriptions[0]);
        RegisterIndicator(_atr);

        // Volatility filter ATR (was AtrVolatilityFilterModule)
        _filterAtr = new Atr(Params.AtrFilter.Period);
        Indicators.Create(_filterAtr, DataSubscriptions[0]);
        RegisterIndicator(_filterAtr);
    }

    protected override void OnContextUpdated(Int64Bar bar, DataSubscription sub)
    {
        var atrValues = _atr.Buffers["Value"];
        if (atrValues.Count > 0)
            Context.Current = atrValues[^1];
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

    protected override int GenerateSignal(Int64Bar bar, Rsi2Context context)
    {
        // Volatility filter gate
        var filterAtrValues = _filterAtr.Buffers["Value"];
        if (filterAtrValues.Count > 0)
        {
            var filterAtr = filterAtrValues[^1];
            if (filterAtr == 0) return 0;
            if (Params.AtrFilter.MinAtr > 0 && filterAtr < Params.AtrFilter.MinAtr) return 0;
            if (Params.AtrFilter.MaxAtr > 0 && filterAtr > Params.AtrFilter.MaxAtr) return 0;
        }

        var rsiValues = _rsi.Buffers["Value"];
        var smaValues = _trendFilter.Buffers["Value"];
        if (rsiValues.Count < Params.RsiPeriod + 1 || smaValues.Count == 0) return 0;

        var rsi = rsiValues[^1];
        var sma = smaValues[^1];
        if (sma == 0) return 0;

        int signal = 0;
        if (rsi < Params.OversoldThreshold && bar.Close > sma)
            signal = 80;  // Buy
        else if (rsi > Params.OverboughtThreshold && bar.Close < sma)
            signal = -80; // Sell

        return Math.Abs(signal) >= Params.SignalThreshold ? signal : 0;
    }

    protected override (long stopLoss, TpLevel[] takeProfits) GetRiskLevels(
        Int64Bar bar, OrderSide direction, long entryPrice, Rsi2Context context)
    {
        var atr = context.Current;
        if (atr == 0) atr = bar.Close / 50;
        var distance = (long)(Params.AtrStopMultiplier * atr);
        var sl = direction == OrderSide.Buy
            ? (entryPrice != 0 ? entryPrice : bar.Close) - distance
            : (entryPrice != 0 ? entryPrice : bar.Close) + distance;
        return (sl, []);
    }
}
