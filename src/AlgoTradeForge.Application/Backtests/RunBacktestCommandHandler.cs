using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Events;
using AlgoTradeForge.Application.Persistence;
using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.Events;
using AlgoTradeForge.Domain.Indicators;
using AlgoTradeForge.Domain.Reporting;

namespace AlgoTradeForge.Application.Backtests;

public sealed class RunBacktestCommandHandler(
    BacktestEngine engine,
    BacktestPreparer preparer,
    IMetricsCalculator metricsCalculator,
    IRunSinkFactory runSinkFactory,
    IPostRunPipeline postRunPipeline,
    IRunRepository runRepository) : ICommandHandler<RunBacktestCommand, BacktestResultDto>
{
    public async Task<BacktestResultDto> HandleAsync(RunBacktestCommand command, CancellationToken ct = default)
    {
        var startedAt = DateTimeOffset.UtcNow;

        var setup = await preparer.PrepareAsync(command, PassthroughIndicatorFactory.Instance, ct);

        var identity = new RunIdentity
        {
            StrategyName = command.StrategyName,
            StrategyVersion = setup.Strategy.Version,
            AssetName = command.AssetName,
            StartTime = command.StartTime,
            EndTime = command.EndTime,
            InitialCash = setup.Options.InitialCash,
            RunMode = ExportMode.Backtest,
            RunTimestamp = startedAt,
            StrategyParameters = command.StrategyParameters,
        };

        var sink = runSinkFactory.Create(identity);
        var eventBus = new EventBus(ExportMode.Backtest, [sink]);
        var result = engine.Run(setup.Series, setup.Strategy, setup.Options, ct, bus: eventBus);

        var runSummary = new RunSummary(
            result.TotalBarsProcessed,
            result.EquityCurve.Count > 0 ? result.EquityCurve[^1].Value : setup.Options.InitialCash,
            result.Fills.Count,
            result.Duration);

        sink.WriteMeta(runSummary);
        postRunPipeline.Execute(sink.RunFolderPath, identity, runSummary);

        // Extract raw long values for metrics calculation (Domain-scale)
        var equityValues = result.EquityCurve.Select(e => e.Value).ToList();
        var metrics = metricsCalculator.Calculate(
            result.Fills, equityValues, setup.Options.InitialCash,
            command.StartTime, command.EndTime);

        var scaledMetrics = metrics with
        {
            InitialCapital = metrics.InitialCapital / setup.ScaleFactor,
            FinalEquity = metrics.FinalEquity / setup.ScaleFactor,
            NetProfit = metrics.NetProfit / setup.ScaleFactor,
            GrossProfit = metrics.GrossProfit / setup.ScaleFactor,
            GrossLoss = metrics.GrossLoss / setup.ScaleFactor,
            TotalCommissions = metrics.TotalCommissions / setup.ScaleFactor,
            AverageWin = metrics.AverageWin / (double)setup.ScaleFactor,
            AverageLoss = metrics.AverageLoss / (double)setup.ScaleFactor,
        };

        var runId = Guid.NewGuid();
        var completedAt = DateTimeOffset.UtcNow;

        // Extract data subscription from the first (primary) subscription
        var primarySub = setup.Strategy.DataSubscriptions[0];

        // Build equity curve with timestamps, scaling values to real-money decimals
        var equityCurve = new EquityPoint[result.EquityCurve.Count];
        for (var i = 0; i < result.EquityCurve.Count; i++)
            equityCurve[i] = new EquityPoint(result.EquityCurve[i].TimestampMs, result.EquityCurve[i].Value / setup.ScaleFactor);

        var record = new BacktestRunRecord
        {
            Id = runId,
            StrategyName = command.StrategyName,
            StrategyVersion = setup.Strategy.Version,
            Parameters = command.StrategyParameters?.AsReadOnly()
                ?? (IReadOnlyDictionary<string, object>)new Dictionary<string, object>(),
            AssetName = primarySub.Asset.Name,
            Exchange = primarySub.Asset.Exchange,
            TimeFrame = TimeFrameFormatter.Format(primarySub.TimeFrame),
            InitialCash = command.InitialCash,
            Commission = command.CommissionPerTrade,
            SlippageTicks = checked((int)command.SlippageTicks),
            StartedAt = startedAt,
            CompletedAt = completedAt,
            DataStart = command.StartTime,
            DataEnd = command.EndTime,
            DurationMs = (long)result.Duration.TotalMilliseconds,
            TotalBars = result.TotalBarsProcessed,
            Metrics = scaledMetrics,
            EquityCurve = equityCurve,
            RunFolderPath = sink.RunFolderPath,
            RunMode = "Backtest",
        };

        await runRepository.SaveAsync(record, ct);
        sink.Dispose();

        return new BacktestResultDto
        {
            Id = runId,
            AssetName = command.AssetName,
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
            CompletedAt = completedAt
        };
    }
}
