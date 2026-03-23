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
    IRunSinkFactory runSinkFactory,
    IPostRunPipeline postRunPipeline) : ICommandHandler<StartDebugSessionCommand, DebugSessionDto>
{
    public async Task<DebugSessionDto> HandleAsync(StartDebugSessionCommand command, CancellationToken ct = default)
    {
        var session = sessionStore.Create(command.DataSubscription.AssetName, command.StrategyName);

        IRunSink? sink = null;
        RunIdentity? capturedIdentity = null;
        BacktestSetup setup;
        try
        {
            setup = await preparer.PrepareAsync(command, options =>
            {
                capturedIdentity = new RunIdentity
                {
                    StrategyName = command.StrategyName,
                    AssetName = command.DataSubscription.AssetName,
                    StartTime = command.BacktestSettings.StartTime,
                    EndTime = command.BacktestSettings.EndTime,
                    InitialCash = options.InitialCash,
                    RunMode = ExportMode.Backtest,
                    RunTimestamp = session.CreatedAt,
                    StrategyParameters = command.StrategyParameters,
                };

                sink = runSinkFactory.Create(capturedIdentity);
                session.EventSink = sink;
                var wsSink = new WebSocketSink();
                session.WebSocketSink = wsSink;
                session.EventBus = new EventBus(ExportMode.Backtest, [sink, wsSink], session.Probe);
                return new EmittingIndicatorFactory(session.EventBus);
            }, ct);
        }
        catch
        {
            // Clean up the orphaned session so it doesn't consume a slot in the store
            if (sessionStore.TryRemove(session.Id, out var orphaned))
                await orphaned!.DisposeAsync();
            throw;
        }

        var runSink = sink ?? throw new InvalidOperationException("Indicator factory callback was not invoked.");

        // Debug sessions must export bar/indicator events for the visual debugger.
        // The default IsExportable=false is correct for normal backtests and optimization,
        // but debug sessions need all subscriptions exportable so events reach the WS sink.
        for (var i = 0; i < setup.Strategy.DataSubscriptions.Count; i++)
        {
            var sub = setup.Strategy.DataSubscriptions[i];
            if (!sub.IsExportable)
                setup.Strategy.DataSubscriptions[i] = sub with { IsExportable = true };
        }

        session.RunTask = Task.Factory.StartNew(
            () =>
            {
                var result = engine.Run(setup.Series, setup.Strategy, setup.Options, session.Cts.Token, session.Probe, session.EventBus);

                var runSummary = new RunSummary(
                    result.TotalBarsProcessed,
                    result.EquityCurve.Count > 0 ? result.EquityCurve[^1].Value : setup.Options.InitialCash,
                    result.Fills.Count,
                    result.Duration);

                runSink.WriteMeta(runSummary);
                postRunPipeline.Execute(runSink.RunFolderPath, capturedIdentity!, runSummary);

                var equityValues = result.EquityCurve.Select(e => e.Value).ToList();
                var (metrics, _) = metricsCalculator.Calculate(
                    result.Fills, equityValues, setup.Options.InitialCash,
                    command.BacktestSettings.StartTime, command.BacktestSettings.EndTime);

                var scaledMetrics = MetricsScaler.ScaleDown(metrics, setup.Scale);

                return new BacktestResultDto
                {
                    Id = session.Id,
                    AssetName = command.DataSubscription.AssetName,
                    StrategyName = command.StrategyName,
                    InitialCapital = scaledMetrics.InitialCapital,
                    FinalEquity = scaledMetrics.FinalEquity,
                    NetProfit = scaledMetrics.NetProfit,
                    TotalCommissions = scaledMetrics.TotalCommissions,
                    TotalReturnPct = scaledMetrics.TotalReturnPct,
                    AnnualizedReturnPct = scaledMetrics.AnnualizedReturnPct,
                    SharpeRatio = scaledMetrics.SharpeRatio,
                    SortinoRatio = scaledMetrics.SortinoRatio,
                    MaxDrawdownPct = scaledMetrics.MaxDrawdownPct,
                    TotalTrades = scaledMetrics.TotalTrades,
                    WinRatePct = scaledMetrics.WinRatePct,
                    ProfitFactor = scaledMetrics.ProfitFactor,
                    TradingDays = scaledMetrics.TradingDays,
                    Duration = result.Duration,
                    CompletedAt = DateTimeOffset.UtcNow
                };
            },
            session.Cts.Token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        return new DebugSessionDto(session.Id, command.DataSubscription.AssetName, command.StrategyName, session.CreatedAt);
    }
}
