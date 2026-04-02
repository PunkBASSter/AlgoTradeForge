using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Indicators;
using AlgoTradeForge.Domain.Optimization.Attributes;

namespace AlgoTradeForge.Domain.Strategy.Modules.Regime;

/// <summary>
/// Detects market regime (Trending vs RangeBound) using ADX.
/// <para><b>Stub:</b> ADX indicator is not yet implemented. This module currently
/// always returns <see cref="MarketRegime.Unknown"/>. Do not rely on regime
/// detection until the ADX class is available (Phase 6).</para>
/// </summary>
[ModuleKey("regime-detector")]
public sealed class RegimeDetectorModule(RegimeDetectorParams parameters)
    : IStrategyModule<RegimeDetectorParams>
{
    private IIndicator<Int64Bar, double>? _adx = null;
    private bool _initialized;

    public void Initialize(IIndicatorFactory factory, DataSubscription subscription)
    {
        // TODO: Create Adx indicator here once the class is implemented (Phase 6).
        // Until then, Update() always sets regime to Unknown.
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
        context.CurrentRegime = adxValue > parameters.TrendThreshold
            ? MarketRegime.Trending
            : MarketRegime.RangeBound;
    }
}
