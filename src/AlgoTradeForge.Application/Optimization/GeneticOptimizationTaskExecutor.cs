using System.Collections.Concurrent;
using System.Diagnostics;
using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Persistence;
using AlgoTradeForge.Application.Progress;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Optimization;
using AlgoTradeForge.Domain.Optimization.Fitness;
using AlgoTradeForge.Domain.Optimization.Genetic;
using AlgoTradeForge.Domain.Optimization.Space;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.Subscriptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.Application.Optimization;

/// <summary>
/// Context captured at enqueue time for a genetic optimization task.
/// Stored on ComputeTask.ExecutionContext.
/// </summary>
public sealed record GeneticExecutionContext
{
    public required string StrategyName { get; init; }
    public required BacktestSettingsDto BacktestSettings { get; init; }
    public required IReadOnlyList<DataFeedSubscription> Subscriptions { get; init; }
    public required List<ResolvedAxis> ActiveAxes { get; init; }
    public required GeneticConfig GaConfig { get; init; }
    public required int MaxParallelism { get; init; }
    public required int MaxTrialsToKeep { get; init; }
    public required ITrialFilterOptions FilterOptions { get; init; }
    public required IParameterNormalizer? Normalizer { get; init; }
    public required Guid GroupId { get; init; }
    public required string GroupRunKey { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public string? InputJson { get; init; }
}

/// <summary>
/// Results from executing a genetic optimization.
/// </summary>
public sealed record GeneticExecutionResult
{
    public required IReadOnlyList<BacktestRunRecord> Trials { get; init; }
    public required IReadOnlyList<FailedTrialRecord> FailedTrialDetails { get; init; }
    public required long FilteredTrials { get; init; }
    public required long FailedTrials { get; init; }
    public required long TotalEvaluations { get; init; }
    public required string? StrategyVersion { get; init; }
    public required long DurationMs { get; init; }
    public required int GenerationsCompleted { get; init; }
}

/// <summary>
/// Executes a genetic optimization for a single DSS. Extracted from RunGeneticOptimizationCommandHandler
/// to be called by the ComputeQueueConsumer.
/// </summary>
public sealed class GeneticOptimizationTaskExecutor(
    IOptimizationStrategyFactory strategyFactory,
    OptimizationSetupHelper helper,
    RunProgressCache progressCache,
    IOptions<RunTimeoutOptions> timeoutOptions,
    ILogger<GeneticOptimizationTaskExecutor> logger)
{
    public async Task<GeneticExecutionResult> ExecuteAsync(
        GeneticExecutionContext ctx,
        Guid childRunId,
        int dssIndex,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        // 1. Load market data for this DSS
        var settings = ctx.BacktestSettings;
        var fromDate = DateOnly.FromDateTime(settings.StartTime.UtcDateTime);
        var toDate = DateOnly.FromDateTime(settings.EndTime.UtcDateTime);

        var resolvedSubs = new List<DataSubscription>();
        var dataCache = new Dictionary<string, (Asset Asset, TimeSeries<Int64Bar> Series)>();
        foreach (var sub in ctx.Subscriptions)
            await helper.ResolveAndCacheAsync(sub, resolvedSubs, dataCache, fromDate, toDate, ct);

        // 2. Set up trial infrastructure
        var maxParallelism = ctx.MaxParallelism > 0
            ? Math.Min(ctx.MaxParallelism, Environment.ProcessorCount)
            : Environment.ProcessorCount;

        var filter = new TrialFilter(ctx.FilterOptions);
        var fitnessFunction = new CompositeFitnessFunction(ctx.GaConfig.Fitness);
        var topTrials = new BoundedTrialQueue(ctx.MaxTrialsToKeep, fitnessFunction);
        var failedTrials = new FailedTrialCollector(capacity: 100);
        long filteredOut = 0;
        long failedCount = 0;
        string? strategyVersion = null;
        var generationsCompleted = 0;

        var trialTimeout = timeoutOptions.Value.BacktestTimeout;

        // 3. Run GA loop
        var ga = new GeneticAlgorithm(ctx.GaConfig);
        var rng = new Random();
        var cache = GeneticFitnessCache.Create(ctx.GaConfig);
        long totalEvals = 0;

        var population = ga.CreateInitialPopulation(ctx.ActiveAxes, rng);
        var bestFitness = double.MinValue;
        var stagnation = 0;

        for (var gen = 0; gen < ctx.GaConfig.MaxGenerations; gen++)
        {
            ct.ThrowIfCancellationRequested();

            var elitesSkipped = population.Count(c => c.Fitness != double.MinValue);
            var hitsBefore = cache?.ReadHits() ?? 0;

            // Evaluate population in parallel
            EvaluatePopulation(
                population, ctx.StrategyName, ctx.BacktestSettings,
                strategyFactory, dataCache, resolvedSubs,
                fitnessFunction, filter, topTrials, failedTrials,
                childRunId, ctx.StartedAt, ref strategyVersion,
                ref filteredOut, ref failedCount,
                cache, ctx.Normalizer, maxParallelism, trialTimeout, ct);

            var cacheHitsThisGen = (cache?.ReadHits() ?? 0) - hitsBefore;
            totalEvals += population.Count - elitesSkipped - cacheHitsThisGen;
            generationsCompleted = gen + 1;

            await progressCache.SetProgressAsync(
                childRunId, totalEvals, ctx.GaConfig.MaxEvaluations, CancellationToken.None);

            var genBest = population.Max(c => c.Fitness);
            if (genBest > bestFitness)
            {
                bestFitness = genBest;
                stagnation = 0;
            }
            else
            {
                stagnation++;
            }

            logger.LogDebug(
                "GA {RunId} DSS[{DssIndex}] gen {Gen}: best={Best:F4}, stagnation={Stagnation}, evals={Evals}",
                childRunId, dssIndex, gen, bestFitness, stagnation, totalEvals);

            if (ga.ShouldTerminate(generationsCompleted, totalEvals, stagnation, sw.Elapsed))
                break;

            population = ga.Evolve(population, ctx.ActiveAxes, gen, stagnation, rng);
        }

        sw.Stop();

        await progressCache.SetProgressAsync(
            childRunId, totalEvals, ctx.GaConfig.MaxEvaluations, CancellationToken.None);

        logger.LogInformation(
            "GA {RunId} DSS[{DssIndex}]: {Gens} generations, {Evals} evaluations in {Duration}ms",
            childRunId, dssIndex, generationsCompleted, totalEvals, sw.ElapsedMilliseconds);

        return new GeneticExecutionResult
        {
            Trials = topTrials.DeduplicateAndDrainSorted(),
            FailedTrialDetails = failedTrials.Drain(childRunId),
            FilteredTrials = Interlocked.Read(ref filteredOut),
            FailedTrials = Interlocked.Read(ref failedCount),
            TotalEvaluations = totalEvals,
            StrategyVersion = strategyVersion,
            DurationMs = sw.ElapsedMilliseconds,
            GenerationsCompleted = generationsCompleted,
        };
    }

    private void EvaluatePopulation(
        List<Chromosome> population,
        string strategyName,
        BacktestSettingsDto settings,
        IOptimizationStrategyFactory factory,
        Dictionary<string, (Asset Asset, TimeSeries<Int64Bar> Series)> dataCache,
        List<DataSubscription> resolvedSubs,
        IFitnessFunction fitnessFunction,
        TrialFilter filter,
        BoundedTrialQueue topTrials,
        FailedTrialCollector failedTrials,
        Guid runId,
        DateTimeOffset startedAt,
        ref string? strategyVersion,
        ref long filteredOutCount,
        ref long failedTrialCount,
        GeneticFitnessCache? cache,
        IParameterNormalizer? normalizer,
        int maxParallelism,
        TimeSpan trialTimeout,
        CancellationToken ct)
    {
        var combos = new ParameterCombination[population.Count];
        for (var i = 0; i < population.Count; i++)
        {
            combos[i] = ChromosomeFactory.ToParameterCombination(population[i]);
            if (normalizer is not null)
                combos[i] = normalizer.Normalize(combos[i]);
        }

        var fitnesses = new double[population.Count];
        for (var i = 0; i < fitnesses.Length; i++)
            fitnesses[i] = population[i].Fitness;

        var actualTasks = Math.Min(maxParallelism, population.Count);
        var partitions = Partitioner.Create(
            Enumerable.Range(0, population.Count),
            EnumerablePartitionerOptions.NoBuffering)
            .GetPartitions(actualTasks);

        // Capture ref locals for lambda access
        var localStrategyVersion = strategyVersion;
        long localFiltered = 0;
        long localFailed = 0;

        var tasks = new Task[partitions.Count];
        for (var p = 0; p < tasks.Length; p++)
        {
            var partition = partitions[p];
            tasks[p] = Task.Factory.StartNew(() =>
            {
                using (partition)
                {
                    var trialCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    try
                    {
                        while (partition.MoveNext())
                        {
                            var i = partition.Current;
                            ct.ThrowIfCancellationRequested();

                            if (Volatile.Read(ref fitnesses[i]) != double.MinValue)
                                continue;

                            string? cacheKey = null;
                            if (cache is not null)
                            {
                                if (cache.TryGet(combos[i], out cacheKey, out var cached))
                                {
                                    Volatile.Write(ref fitnesses[i], cached.Fitness);
                                    if (cached.WasFailed)
                                        Interlocked.Increment(ref localFailed);
                                    else if (cached.WasFilteredOut)
                                        Interlocked.Increment(ref localFiltered);
                                    else if (cached.Record is not null)
                                        topTrials.TryAdd(cached.Record);
                                    continue;
                                }
                            }

                            if (!trialCts.TryReset())
                            {
                                trialCts.Dispose();
                                trialCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                            }
                            trialCts.CancelAfter(trialTimeout);

                            // Inject resolved subscriptions
                            var mutableValues = new Dictionary<string, object>(combos[i].Values)
                            {
                                ["DataSubscriptions"] = resolvedSubs
                            };
                            var comboWithSubs = new ParameterCombination(mutableValues);

                            try
                            {
                                var record = helper.ExecuteTrial(
                                    strategyName, settings,
                                    comboWithSubs, factory, dataCache,
                                    runId, startedAt, ref localStrategyVersion, trialCts.Token);

                                var filteredOut = !filter.Passes(record.Metrics);
                                var rawFitness = fitnessFunction.Evaluate(record.Metrics);
                                Volatile.Write(ref fitnesses[i], rawFitness);
                                record = record with { FitnessScore = rawFitness <= double.MinValue ? null : rawFitness };

                                if (!filteredOut)
                                    topTrials.TryAdd(record);
                                else
                                    Interlocked.Increment(ref localFiltered);

                                cache?.TryAdd(cacheKey!, new CachedFitnessEntry(
                                    fitnesses[i], filteredOut, WasFailed: false,
                                    Record: filteredOut ? null : record));
                            }
                            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                            {
                                Interlocked.Increment(ref localFailed);
                                failedTrials.RecordTimeout(combos[i].Values, trialTimeout);
                                cache?.TryAdd(cacheKey!, new CachedFitnessEntry(
                                    double.MinValue, false, WasFailed: true, Record: null));
                            }
                            catch (OperationCanceledException)
                            {
                                throw;
                            }
                            catch (Exception ex)
                            {
                                Interlocked.Increment(ref localFailed);
                                failedTrials.Record(
                                    combos[i].Values,
                                    ex.GetType().FullName ?? ex.GetType().Name,
                                    ex.Message,
                                    ex.StackTrace ?? string.Empty);
                                cache?.TryAdd(cacheKey!, new CachedFitnessEntry(
                                    double.MinValue, false, WasFailed: true, Record: null));
                            }
                        }
                    }
                    finally
                    {
                        trialCts.Dispose();
                    }
                }
            }, ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        Task.WaitAll(tasks);

        // Write back
        for (var i = 0; i < population.Count; i++)
            population[i].Fitness = fitnesses[i];

        strategyVersion = localStrategyVersion;
        Interlocked.Add(ref filteredOutCount, Interlocked.Read(ref localFiltered));
        Interlocked.Add(ref failedTrialCount, Interlocked.Read(ref localFailed));
    }
}
