using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Indicators;
using AlgoTradeForge.Domain.Optimization.Attributes;
using AlgoTradeForge.Domain.Strategy.Modules;
using AlgoTradeForge.Domain.Strategy.Modules.Exit;
using AlgoTradeForge.Domain.Strategy.Modules.Filter;
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
    : ModularStrategyBase<DonchianParams>(parameters, indicators)
{
    public override string Version => "1.0.0";

    private DonchianChannel _entryChannel = null!;
    private DonchianChannel _exitChannel = null!;
    private Atr _atr = null!;
    private TrailingStopModule _trailingStopModule = null!;
    private RegimeChangeExitRule _regimeChangeExit = null!;

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
        SetTrailingStop(_trailingStopModule);

        // Regime detector
        var regimeDetector = new RegimeDetectorModule(Params.RegimeDetectorConfig);
        regimeDetector.Initialize(Indicators, DataSubscriptions[0]);
        SetRegimeDetector(regimeDetector);

        // Regime filter: only allow Trending
        var regimeFilter = new RegimeFilterModule(Context, MarketRegime.Trending);
        AddFilter(regimeFilter);

        // Exit rules: regime change
        _regimeChangeExit = new RegimeChangeExitRule();
        var exitModule = new ExitModule();
        exitModule.AddRule(_regimeChangeExit);
        SetExit(exitModule);
    }

    protected override void OnContextUpdated(Int64Bar bar, DataSubscription sub)
    {
        var atrValues = _atr.Buffers["Value"];
        if (atrValues.Count > 0)
            Context.CurrentAtr = atrValues[^1];
    }

    protected override int OnGenerateSignal(Int64Bar bar, StrategyContext context)
    {
        var upper = _entryChannel.Buffers["Upper"];
        var lower = _entryChannel.Buffers["Lower"];
        if (upper.Count < 2) return 0;

        var prevUpper = upper[^2];
        var prevLower = lower[^2];
        if (prevUpper == 0 || prevLower == 0) return 0;

        // Breakout above previous bar's upper channel
        if (bar.High > prevUpper)
            return 80;  // Buy

        // Breakout below previous bar's lower channel
        if (bar.Low < prevLower)
            return -80; // Sell

        return 0;
    }

    protected override (long price, OrderType type) OnGetEntryPrice(
        Int64Bar bar, OrderSide direction, StrategyContext context)
    {
        var upper = _entryChannel.Buffers["Upper"];
        var lower = _entryChannel.Buffers["Lower"];
        if (upper.Count < 2) return (0, OrderType.Market);

        // Stop order at the previous bar's channel boundary
        var price = direction == OrderSide.Buy ? upper[^2] : lower[^2];
        return (price, OrderType.Stop);
    }

    protected override (long stopLoss, TpLevel[] takeProfits) OnGetRiskLevels(
        Int64Bar bar, OrderSide direction, long entryPrice, StrategyContext context)
    {
        var atr = context.CurrentAtr;
        if (atr == 0) atr = bar.Close / 50;

        var distance = (long)(Params.AtrStopMultiplier * atr);
        var sl = direction == OrderSide.Buy
            ? entryPrice - distance
            : entryPrice + distance;

        return (sl, []);
    }

    protected override void OnOrderFilled(Fill fill, Order order)
    {
        // Activate trailing stop on entry fill
        // Find the group this fill belongs to
        var registry = ((ITradeRegistryProvider)this).TradeRegistry;
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
    }
}
