using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Indicators;
using AlgoTradeForge.Domain.Optimization.Attributes;
using AlgoTradeForge.Domain.Strategy.Modules;
using AlgoTradeForge.Domain.Strategy.Modules.Filter;
using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.Domain.Strategy.Rsi2MeanReversion;

[StrategyKey("RSI2-MeanReversion")]
public sealed class Rsi2MeanReversionStrategy(
    Rsi2Params parameters, IIndicatorFactory? indicators = null)
    : ModularStrategyBase<Rsi2Params>(parameters, indicators)
{
    public override string Version => "1.0.0";

    private Rsi _rsi = null!;
    private Sma _trendFilter = null!;
    private Atr _atr = null!;

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

        var filter = new AtrVolatilityFilterModule(Params.AtrFilter);
        filter.Initialize(Indicators, DataSubscriptions[0]);
        AddFilter(filter);
    }

    protected override void OnContextUpdated(Int64Bar bar, DataSubscription sub)
    {
        var atrValues = _atr.Buffers["Value"];
        if (atrValues.Count > 0)
            Context.CurrentAtr = atrValues[^1];
    }

    protected override int OnGenerateSignal(Int64Bar bar, StrategyContext context)
    {
        var rsiValues = _rsi.Buffers["Value"];
        var smaValues = _trendFilter.Buffers["Value"];
        if (rsiValues.Count < Params.RsiPeriod + 1 || smaValues.Count == 0) return 0;

        var rsi = rsiValues[^1];
        var sma = smaValues[^1];
        if (sma == 0) return 0;

        if (rsi < Params.OversoldThreshold && bar.Close > sma)
            return 80;  // Buy

        if (rsi > Params.OverboughtThreshold && bar.Close < sma)
            return -80; // Sell

        return 0;
    }
}
