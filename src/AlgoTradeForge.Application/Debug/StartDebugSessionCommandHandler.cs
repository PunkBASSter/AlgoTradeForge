using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Backtests;
using AlgoTradeForge.Application.Events;
using AlgoTradeForge.Application.Indicators;
using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.Events;
using AlgoTradeForge.Domain.Reporting;

namespace AlgoTradeForge.Application.Debug;

public sealed class StartDebugSessionCommandHandler(
    BacktestEngine engine,
    BacktestPreparer preparer,
    IMetricsCalculator metricsCalculator,
    IDebugSessionStore sessionStore,
    IRunSinkFactory runSinkFactory) : ICommandHandler<StartDebugSessionCommand, DebugSessionDto>
{
    public async Task<DebugSessionDto> HandleAsync(StartDebugSessionCommand command, CancellationToken ct = default)
    {
        var session = sessionStore.Create(command.AssetName, command.StrategyName);

        IRunSink? sink = null;
        var setup = await preparer.PrepareAsync(command, options =>
        {
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

            sink = runSinkFactory.Create(identity);
            session.EventSink = sink;
            session.EventBus = new EventBus(ExportMode.Backtest, [sink]);
            return new EmittingIndicatorFactory(session.EventBus);
        }, ct);

        var runSink = sink ?? throw new InvalidOperationException("Indicator factory callback was not invoked.");

        session.RunTask = Task.Factory.StartNew(
            () =>
            {
                var result = engine.Run(setup.Series, setup.Strategy, setup.Options, session.Cts.Token, session.Probe, session.EventBus);

                runSink.WriteMeta(new RunSummary(
                    result.TotalBarsProcessed,
                    result.EquityCurve.Count > 0 ? result.EquityCurve[^1] : setup.Options.InitialCash,
                    result.Fills.Count,
                    result.Duration));

                var metrics = metricsCalculator.Calculate(
                    result.Fills, result.EquityCurve, setup.Options.InitialCash,
                    command.StartTime, command.EndTime);

                return new BacktestResultDto
                {
                    Id = session.Id,
                    AssetName = command.AssetName,
                    StrategyName = command.StrategyName,
                    InitialCapital = metrics.InitialCapital / setup.ScaleFactor,
                    FinalEquity = metrics.FinalEquity / setup.ScaleFactor,
                    NetProfit = metrics.NetProfit / setup.ScaleFactor,
                    TotalCommissions = metrics.TotalCommissions / setup.ScaleFactor,
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
