using System.Diagnostics;
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
    private static readonly TimeSpan ProgressFlushInterval = TimeSpan.FromSeconds(1);

    public async Task<BacktestSubmissionDto> HandleAsync(RunBacktestCommand command, CancellationToken ct = default)
    {
        // 1. Compute RunKey and check for dedup
        var runKey = RunKeyBuilder.Build(command);
        var existingId = await progressCache.TryGetRunIdByKeyAsync(runKey, ct);
        if (existingId is not null)
        {
            var existing = await progressCache.GetAsync(existingId.Value, ct);
            if (existing is not null && existing.Status is RunStatus.Pending or RunStatus.Running)
            {
                return new BacktestSubmissionDto
                {
                    Id = existingId.Value,
                    TotalBars = (int)existing.Total,
                };
            }

            // Stale mapping — clean up
            await progressCache.RemoveRunKeyAsync(runKey, ct);
        }

        // 2. Synchronous validation and data loading
        var startedAt = DateTimeOffset.UtcNow;
        var setup = await preparer.PrepareAsync(command, PassthroughIndicatorFactory.Instance, ct);
        var totalBars = setup.Series[0].Count;

        // 3. Create RunProgressEntry and store in cache
        var runId = Guid.NewGuid();
        var entry = new RunProgressEntry
        {
            Id = runId,
            Status = RunStatus.Pending,
            Processed = 0,
            Failed = 0,
            Total = totalBars,
            StartedAt = startedAt
        };
        await progressCache.SetAsync(entry, ct);
        await progressCache.SetRunKeyAsync(runKey, runId, ct);

        // 4. Register CancellationTokenSource
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
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
            // Update status to Running
            await progressCache.SetAsync(new RunProgressEntry
            {
                Id = runId,
                Status = RunStatus.Running,
                Processed = 0,
                Failed = 0,
                Total = totalBars,
                StartedAt = startedAt
            }, ct);

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

            var progressSink = new ProgressTrackingEventBusSink();
            using var fileSink = runSinkFactory.Create(identity);
            var eventBus = new EventBus(ExportMode.Backtest, [fileSink, progressSink]);

            // Start progress flush loop
            var progressFlushTask = FlushProgressAsync(runId, progressSink, totalBars, startedAt, ct);

            var result = engine.Run(setup.Series, setup.Strategy, setup.Options, ct, bus: eventBus);

            // Final progress flush
            await progressCache.SetAsync(new RunProgressEntry
            {
                Id = runId,
                Status = RunStatus.Running,
                Processed = progressSink.ProcessedBars,
                Failed = 0,
                Total = totalBars,
                StartedAt = startedAt
            });

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

            // Update cache to Completed
            await progressCache.SetAsync(new RunProgressEntry
            {
                Id = runId,
                Status = RunStatus.Completed,
                Processed = totalBars,
                Failed = 0,
                Total = totalBars,
                StartedAt = startedAt
            });

            logger.LogInformation("Backtest {RunId} completed in {Duration}ms", runId, record.DurationMs);
        }
        catch (OperationCanceledException)
        {
            await progressCache.SetAsync(new RunProgressEntry
            {
                Id = runId,
                Status = RunStatus.Cancelled,
                Processed = 0,
                Failed = 0,
                Total = totalBars,
                StartedAt = startedAt
            });

            logger.LogInformation("Backtest {RunId} was cancelled", runId);
        }
        catch (Exception ex)
        {
            await progressCache.SetAsync(new RunProgressEntry
            {
                Id = runId,
                Status = RunStatus.Failed,
                Processed = 0,
                Failed = 0,
                Total = totalBars,
                ErrorMessage = ex.Message,
                ErrorStackTrace = ex.StackTrace,
                StartedAt = startedAt
            });

            logger.LogError(ex, "Backtest {RunId} failed", runId);
        }
        finally
        {
            // Cleanup: remove RunKey mapping and CTS immediately
            await progressCache.RemoveRunKeyAsync(runKey);
            cancellationRegistry.Remove(runId);

            // Delayed progress entry cleanup — allows final status polls to read terminal state
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(60));
                await progressCache.RemoveAsync(runId);
            });
        }
    }

    private async Task FlushProgressAsync(
        Guid runId,
        ProgressTrackingEventBusSink progressSink,
        int totalBars,
        DateTimeOffset startedAt,
        CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(ProgressFlushInterval, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            await progressCache.SetAsync(new RunProgressEntry
            {
                Id = runId,
                Status = RunStatus.Running,
                Processed = progressSink.ProcessedBars,
                Failed = 0,
                Total = totalBars,
                StartedAt = startedAt
            });
        }
    }
}
