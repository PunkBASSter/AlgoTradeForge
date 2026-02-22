using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Events;
using AlgoTradeForge.Application.Persistence;
using AlgoTradeForge.Application.Progress;
using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.Events;
using AlgoTradeForge.Domain.Indicators;
using AlgoTradeForge.Domain.Reporting;
using Microsoft.Extensions.Logging;

namespace AlgoTradeForge.Application.Backtests;

public sealed class RunBacktestCommandHandler(
    BacktestEngine engine,
    BacktestPreparer preparer,
    IMetricsCalculator metricsCalculator,
    IRunSinkFactory runSinkFactory,
    IPostRunPipeline postRunPipeline,
    IRunRepository runRepository,
    RunProgressCache progressCache,
    IRunCancellationRegistry cancellationRegistry,
    ILogger<RunBacktestCommandHandler> logger) : ICommandHandler<RunBacktestCommand, BacktestSubmissionDto>
{
    public async Task<BacktestSubmissionDto> HandleAsync(RunBacktestCommand command, CancellationToken ct = default)
    {
        // 1. Compute RunKey and check for dedup
        var runKey = RunKeyBuilder.Build(command);
        var existingId = await progressCache.TryGetRunIdByKeyAsync(runKey, ct);
        if (existingId is not null)
        {
            var existing = await progressCache.GetProgressAsync(existingId.Value, ct);
            if (existing is not null)
            {
                return new BacktestSubmissionDto
                {
                    Id = existingId.Value,
                    TotalBars = (int)existing.Value.Total,
                };
            }

            // Stale mapping — clean up
            await progressCache.RemoveRunKeyAsync(runKey, ct);
        }

        // 2. Synchronous validation and data loading
        var startedAt = DateTimeOffset.UtcNow;
        var setup = await preparer.PrepareAsync(command, PassthroughIndicatorFactory.Instance, ct);
        var totalBars = setup.Series[0].Count;

        // 3. Store progress marker in cache
        var runId = Guid.NewGuid();
        await progressCache.SetProgressAsync(runId, 0, totalBars, ct);
        await progressCache.SetRunKeyAsync(runKey, runId, ct);

        // 4. Register CancellationTokenSource
        var cts = new CancellationTokenSource();
        cancellationRegistry.Register(runId, cts);

        // 5. Start background task
        _ = Task.Run(() => RunBacktestAsync(command, setup, runId, runKey, startedAt, totalBars, cts.Token));

        // 6. Return immediately
        return new BacktestSubmissionDto
        {
            Id = runId,
            TotalBars = totalBars,
        };
    }

    private async Task RunBacktestAsync(
        RunBacktestCommand command,
        BacktestSetup setup,
        Guid runId,
        string runKey,
        DateTimeOffset startedAt,
        int totalBars,
        CancellationToken ct)
    {
        try
        {
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

            using var fileSink = runSinkFactory.Create(identity);
            var eventBus = new EventBus(ExportMode.Backtest, [fileSink]);

            var result = engine.Run(setup.Series, setup.Strategy, setup.Options, ct, bus: eventBus);

            var runSummary = new RunSummary(
                result.TotalBarsProcessed,
                result.EquityCurve.Count > 0 ? result.EquityCurve[^1].Value : setup.Options.InitialCash,
                result.Fills.Count,
                result.Duration);

            fileSink.WriteMeta(runSummary);
            postRunPipeline.Execute(fileSink.RunFolderPath, identity, runSummary);

            // Calculate metrics
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

            var completedAt = DateTimeOffset.UtcNow;
            var primarySub = setup.Strategy.DataSubscriptions[0];

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
                RunFolderPath = fileSink.RunFolderPath,
                RunMode = "Backtest",
            };

            await runRepository.SaveAsync(record);

            logger.LogInformation("Backtest {RunId} completed in {Duration}ms", runId, record.DurationMs);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Backtest {RunId} was cancelled", runId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Backtest {RunId} failed", runId);
        }
        finally
        {
            await progressCache.RemoveProgressAsync(runId);
            await progressCache.RemoveRunKeyAsync(runKey);
            cancellationRegistry.Remove(runId);
        }
    }
}
