using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Indicators;
using AlgoTradeForge.Domain.Optimization.Attributes;

namespace AlgoTradeForge.Domain.Strategy.Modules.Regime;

/// <summary>
/// Detects market regime (Trending vs RangeBound) using ADX.
/// ADX above TrendThreshold → Trending, below → RangeBound.
/// Returns Unknown during indicator warmup.
/// </summary>
[ModuleKey("regime-detector")]
public sealed class RegimeDetectorModule(RegimeDetectorParams parameters)
    : IStrategyModule<RegimeDetectorParams>
{
    private IIndicator<Int64Bar, double>? _adx;
    private bool _initialized;

    public void Initialize(IIndicatorFactory factory, DataSubscription subscription)
    {
        _adx = factory.Create<Int64Bar, double>(new Adx(parameters.AdxPeriod), subscription);
        _initialized = true;
    }

    public void Update(Int64Bar bar, StrategyContext context)
    {
        if (!_initialized || _adx is null)
        {
            context.CurrentRegime = MarketRegime.Unknown;
            return;
        }

        var values = _adx.Buffers["Value"];
        if (values.Count == 0)
        {
            context.CurrentRegime = MarketRegime.Unknown;
            return;
        }

        var adxValue = values[^1];

        // ADX outputs 0.0 during warmup period — treat as Unknown
        if (adxValue == 0.0)
        {
            context.CurrentRegime = MarketRegime.Unknown;
            return;
        }

        context.CurrentRegime = adxValue > parameters.TrendThreshold
            ? MarketRegime.Trending
            : MarketRegime.RangeBound;
    }
}
