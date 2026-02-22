using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Events;
using AlgoTradeForge.Application.Persistence;
using AlgoTradeForge.Application.Progress;
using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.Events;
using AlgoTradeForge.Domain.Indicators;
using AlgoTradeForge.Domain.Reporting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
    IOptions<RunTimeoutOptions> timeoutOptions,
    ILogger<RunBacktestCommandHandler> logger) : ICommandHandler<RunBacktestCommand, BacktestSubmissionDto>
{
    private const int ProgressUpdateInterval = 100;

    public async Task<BacktestSubmissionDto> HandleAsync(RunBacktestCommand command, CancellationToken ct = default)
    {
        // 1. Compute RunKey and check for dedup (under lock to prevent TOCTOU races)
        var runKey = RunKeyBuilder.Build(command);
        using (await progressCache.AcquireRunKeyLockAsync(runKey, ct))
        {
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

            // 3. Store progress marker in cache (still under lock)
            var runId = Guid.NewGuid();
            await progressCache.SetProgressAsync(runId, 0, totalBars, ct);
            await progressCache.SetRunKeyAsync(runKey, runId, ct);

            // 4. Register CancellationTokenSource with timeout safety net
            var cts = new CancellationTokenSource(timeoutOptions.Value.BacktestTimeout);
            cancellationRegistry.Register(runId, cts);

            // 5. Start background task on a dedicated thread (engine.Run is synchronous & CPU-bound)
            _ = Task.Factory.StartNew(
                () => RunBacktestAsync(command, setup, runId, runKey, startedAt, totalBars, cts.Token),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

            // 6. Return immediately
            return new BacktestSubmissionDto
            {
                Id = runId,
                TotalBars = totalBars,
            };
        }
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

            var result = engine.Run(setup.Series, setup.Strategy, setup.Options, ct, bus: eventBus,
                onBarsProcessed: bars =>
                {
                    if (bars % ProgressUpdateInterval == 0)
                        _ = progressCache.SetProgressAsync(runId, bars, totalBars);
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

            var scaledMetrics = MetricsScaler.ScaleDown(metrics, setup.ScaleFactor);

            var completedAt = DateTimeOffset.UtcNow;
            var primarySub = setup.Strategy.DataSubscriptions[0];

            var equityCurve = MetricsScaler.ScaleEquityCurve(result.EquityCurve, setup.ScaleFactor);

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
                RunMode = RunModes.Backtest,
            };

            await runRepository.SaveAsync(record);

            logger.LogInformation("Backtest {RunId} completed in {Duration}ms", runId, record.DurationMs);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Backtest {RunId} was cancelled", runId);
            await SaveErrorRecordAsync(command, setup, runId, startedAt, RunModes.Cancelled, "Run was cancelled by user.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Backtest {RunId} failed", runId);
            await SaveErrorRecordAsync(command, setup, runId, startedAt, RunModes.Failed, ex.Message, ex.StackTrace);
        }
        finally
        {
            await progressCache.RemoveProgressAsync(runId);
            await progressCache.RemoveRunKeyAsync(runKey);
            cancellationRegistry.Remove(runId);
        }
    }

    private async Task SaveErrorRecordAsync(
        RunBacktestCommand command, BacktestSetup setup, Guid runId,
        DateTimeOffset startedAt, string runMode, string errorMessage, string? errorStackTrace = null)
    {
        try
        {
            var completedAt = DateTimeOffset.UtcNow;
            var primarySub = setup.Strategy.DataSubscriptions[0];
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
                DurationMs = (long)(completedAt - startedAt).TotalMilliseconds,
                TotalBars = 0,
                Metrics = new PerformanceMetrics
                {
                    TotalTrades = 0, WinningTrades = 0, LosingTrades = 0,
                    NetProfit = 0, GrossProfit = 0, GrossLoss = 0, TotalCommissions = 0,
                    TotalReturnPct = 0, AnnualizedReturnPct = 0,
                    SharpeRatio = 0, SortinoRatio = 0, MaxDrawdownPct = 0,
                    WinRatePct = 0, ProfitFactor = 0, AverageWin = 0, AverageLoss = 0,
                    InitialCapital = command.InitialCash, FinalEquity = command.InitialCash,
                    TradingDays = 0,
                },
                EquityCurve = [],
                RunFolderPath = null,
                RunMode = runMode,
                ErrorMessage = errorMessage,
                ErrorStackTrace = errorStackTrace,
            };
            await runRepository.SaveAsync(record);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save error record for backtest {RunId}", runId);
        }
    }
}
