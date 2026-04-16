using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Indicators;
using AlgoTradeForge.Domain.Optimization.Attributes;
using AlgoTradeForge.Domain.Strategy.Modules;
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

    protected override int OnGenerateSignal(Int64Bar bar, Rsi2Context context)
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

        if (rsi < Params.OversoldThreshold && bar.Close > sma)
            return 80;  // Buy

        if (rsi > Params.OverboughtThreshold && bar.Close < sma)
            return -80; // Sell

        return 0;
    }
}
