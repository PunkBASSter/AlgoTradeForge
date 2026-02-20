using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Backtests;
using AlgoTradeForge.Application.Events;
using AlgoTradeForge.Application.Indicators;
using AlgoTradeForge.Application.Repositories;
using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.Events;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Reporting;
using AlgoTradeForge.Domain.Strategy;

namespace AlgoTradeForge.Application.Debug;

public sealed class StartDebugSessionCommandHandler(
    BacktestEngine engine,
    IAssetRepository assetRepository,
    IStrategyFactory strategyFactory,
    IHistoryRepository historyRepository,
    IMetricsCalculator metricsCalculator,
    IDebugSessionStore sessionStore,
    IRunSinkFactory runSinkFactory) : ICommandHandler<StartDebugSessionCommand, DebugSessionDto>
{
    public async Task<DebugSessionDto> HandleAsync(StartDebugSessionCommand command, CancellationToken ct = default)
    {
        var asset = await assetRepository.GetByNameAsync(command.AssetName, command.Exchange, ct)
            ?? throw new ArgumentException($"Asset '{command.AssetName}' not found.", nameof(command));

        var scaleFactor = 1m / asset.TickSize;

        var options = new BacktestOptions
        {
            InitialCash = (long)(command.InitialCash * scaleFactor),
            Asset = asset,
            StartTime = command.StartTime,
            EndTime = command.EndTime,
            CommissionPerTrade = (long)(command.CommissionPerTrade * scaleFactor),
            SlippageTicks = command.SlippageTicks,
            UseDetailedExecutionLogic = command.UseDetailedExecutionLogic
        };

        var session = sessionStore.Create(command.AssetName, command.StrategyName);

        var identity = new RunIdentity
        {
            StrategyName = command.StrategyName,
            AssetName = command.AssetName,
            StartTime = command.StartTime,
            EndTime = command.EndTime,
            InitialCash = options.InitialCash,
            RunMode = ExportMode.Backtest,
            RunTimestamp = session.CreatedAt,
            StrategyParameters = command.StrategyParameters,
        };

        var sink = runSinkFactory.Create(identity);
        session.EventSink = sink;
        session.EventBus = new EventBus(ExportMode.Backtest, [sink]);
        var indicatorFactory = new EmittingIndicatorFactory(session.EventBus);

        var strategy = strategyFactory.Create(command.StrategyName, indicatorFactory, command.StrategyParameters);

        if (strategy.DataSubscriptions.Count == 0)
        {
            var timeFrame = command.TimeFrame ?? asset.SmallestInterval;
            strategy.DataSubscriptions.Add(new DataSubscription(asset, timeFrame));
        }

        var fromDate = DateOnly.FromDateTime(command.StartTime.UtcDateTime);
        var toDate = DateOnly.FromDateTime(command.EndTime.UtcDateTime);

        var seriesArray = new TimeSeries<Int64Bar>[strategy.DataSubscriptions.Count];
        for (var i = 0; i < strategy.DataSubscriptions.Count; i++)
            seriesArray[i] = historyRepository.Load(strategy.DataSubscriptions[i], fromDate, toDate);

        session.RunTask = Task.Factory.StartNew(
            () =>
            {
                var result = engine.Run(seriesArray, strategy, options, session.Cts.Token, session.Probe, session.EventBus);

                sink.WriteMeta(new RunSummary(
                    result.TotalBarsProcessed,
                    result.EquityCurve.Count > 0 ? result.EquityCurve[^1] : options.InitialCash,
                    result.Fills.Count,
                    result.Duration));

                var metrics = metricsCalculator.Calculate(
                    result.Fills, result.EquityCurve, options.InitialCash,
                    command.StartTime, command.EndTime);

                return new BacktestResultDto
                {
                    Id = session.Id,
                    AssetName = command.AssetName,
                    StrategyName = command.StrategyName,
                    InitialCapital = metrics.InitialCapital / scaleFactor,
                    FinalEquity = metrics.FinalEquity / scaleFactor,
                    NetProfit = metrics.NetProfit / scaleFactor,
                    TotalCommissions = metrics.TotalCommissions / scaleFactor,
                    TotalReturnPct = metrics.TotalReturnPct,
                    AnnualizedReturnPct = metrics.AnnualizedReturnPct,
                    SharpeRatio = metrics.SharpeRatio,
                    SortinoRatio = metrics.SortinoRatio,
                    MaxDrawdownPct = metrics.MaxDrawdownPct,
                    TotalTrades = metrics.TotalTrades,
                    WinRatePct = metrics.WinRatePct,
                    ProfitFactor = metrics.ProfitFactor,
                    TradingDays = metrics.TradingDays,
                    Duration = result.Duration,
                    CompletedAt = DateTimeOffset.UtcNow
                };
            },
            session.Cts.Token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        return new DebugSessionDto(session.Id, command.AssetName, command.StrategyName, session.CreatedAt);
    }
}
