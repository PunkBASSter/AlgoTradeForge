using System.Collections.Concurrent;
using System.Diagnostics;
using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Persistence;
using AlgoTradeForge.Application.Progress;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Optimization.Genetic;
using AlgoTradeForge.Domain.Optimization.Space;
using AlgoTradeForge.Domain.Strategy;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.Application.Optimization;

public sealed class RunGeneticOptimizationCommandHandler(
    IOptimizationStrategyFactory strategyFactory,
    OptimizationSetupHelper helper,
    OptimizationAxisResolver axisResolver,
    RunProgressCache progressCache,
    IRunCancellationRegistry cancellationRegistry,
    IOptions<RunTimeoutOptions> timeoutOptions,
    ILogger<RunGeneticOptimizationCommandHandler> logger) : ICommandHandler<RunGeneticOptimizationCommand, OptimizationSubmissionDto>
{
    public async Task<OptimizationSubmissionDto> HandleAsync(
        RunGeneticOptimizationCommand command, CancellationToken ct = default)
    {
        // 1. Validation and data loading (same pattern as brute-force)
        var descriptor = helper.SpaceProvider.GetDescriptor(command.StrategyName)
            ?? throw new ArgumentException($"Strategy '{command.StrategyName}' not found.");

        var settings = command.BacktestSettings;
        var dataSubs = command.DataSubscriptions;
        var axisSubs = command.SubscriptionAxis;
        var hasDataSubs = dataSubs is { Count: > 0 };
        var hasAxisSubs = axisSubs is { Count: > 0 };

        if (!hasDataSubs && !hasAxisSubs)
            throw new ArgumentException("At least one DataSubscription or SubscriptionAxis entry must be provided.");

        var fromDate = DateOnly.FromDateTime(settings.StartTime.UtcDateTime);
        var toDate = DateOnly.FromDateTime(settings.EndTime.UtcDateTime);

        List<DataSubscriptionDto> fixedDtos;
        List<DataSubscriptionDto> axisDtos;

        if (hasAxisSubs)
        {
            fixedDtos = dataSubs ?? [];
            axisDtos = axisSubs!;
        }
        else if (dataSubs!.Count > 1)
        {
            fixedDtos = [];
            axisDtos = dataSubs;
        }
        else
        {
            fixedDtos = dataSubs;
            axisDtos = [];
        }

        var fixedSubscriptions = new List<DataSubscription>();
        var axisSubscriptions = new List<DataSubscription>();
        var dataCache = new Dictionary<string, (Asset Asset, TimeSeries<Int64Bar> Series)>();

        foreach (var sub in fixedDtos)
            await helper.ResolveAndCacheAsync(sub, fixedSubscriptions, dataCache, fromDate, toDate, ct);
        foreach (var sub in axisDtos)
            await helper.ResolveAndCacheAsync(sub, axisSubscriptions, dataCache, fromDate, toDate, ct);

        var resolvedAxes = axisResolver.Resolve(descriptor, command.Axes);

        if (axisSubscriptions.Count > 0)
        {
            var allAxes = new List<ResolvedAxis>(resolvedAxes)
            {
                new ResolvedDiscreteAxis("DataSubscriptions",
                    axisSubscriptions.Cast<object>().ToList())
            };
            resolvedAxes = allAxes;
        }

        var activeAxes = resolvedAxes
            .Where(a => a switch
            {
                ResolvedNumericAxis n => n.Values.Count > 0,
                ResolvedDiscreteAxis d => d.Values.Count > 0,
                ResolvedModuleSlotAxis m => m.Variants.Count > 0,
                _ => true
            })
            .ToList();

        // 2. Build GA config with auto-sizing
        var gaSettings = command.GeneticSettings;
        var gaConfig = GeneticConfigResolver.Resolve(new GeneticConfig
        {
            PopulationSize = gaSettings.PopulationSize,
            MaxGenerations = gaSettings.MaxGenerations,
            MaxEvaluations = gaSettings.MaxEvaluations,
            EliteCount = gaSettings.EliteCount,
            CrossoverRate = gaSettings.CrossoverRate,
            TournamentSize = gaSettings.TournamentSize,
            StagnationLimit = gaSettings.StagnationLimit,
            TimeBudget = gaSettings.TimeBudget,
            MinTrades = gaSettings.MinTrades,
            MaxDrawdownThreshold = gaSettings.MaxDrawdownThreshold,
            Weights = new FitnessWeights
            {
                SharpeWeight = gaSettings.SharpeWeight,
                SortinoWeight = gaSettings.SortinoWeight,
                ProfitFactorWeight = gaSettings.ProfitFactorWeight,
                AnnualizedReturnWeight = gaSettings.AnnualizedReturnWeight,
            },
        }, activeAxes);

        // 3. Store progress
        var startedAt = DateTimeOffset.UtcNow;
        var runId = Guid.NewGuid();
        await progressCache.SetProgressAsync(runId, 0, gaConfig.MaxEvaluations, ct);

        // 4. Launch background task
        _ = Task.Factory.StartNew(
            () => RunGeneticOptimizationAsync(
                command, fixedSubscriptions, dataCache, activeAxes,
                gaConfig, runId, startedAt, strategyFactory),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        return new OptimizationSubmissionDto
        {
            Id = runId,
            TotalCombinations = gaConfig.MaxEvaluations,
        };
    }

    private async Task RunGeneticOptimizationAsync(
        RunGeneticOptimizationCommand command,
        List<DataSubscription> fixedSubscriptions,
        Dictionary<string, (Asset Asset, TimeSeries<Int64Bar> Series)> dataCache,
        List<ResolvedAxis> activeAxes,
        GeneticConfig gaConfig,
        Guid runId,
        DateTimeOffset startedAt,
        IOptimizationStrategyFactory factory)
    {
        using var cts = new CancellationTokenSource(timeoutOptions.Value.OptimizationTimeout);
        cancellationRegistry.Register(runId, cts);
        var ct = cts.Token;

        var filter = new TrialFilter(command);
        var topTrials = new BoundedTrialQueue(command.MaxTrialsToKeep, command.SortBy);
        var failedTrials = new FailedTrialCollector(capacity: 100);
        var state = new EvalState();
        var generationsCompleted = 0;

        var maxParallelism = command.MaxDegreeOfParallelism > 0
            ? command.MaxDegreeOfParallelism
            : Environment.ProcessorCount;
        var primarySub = OptimizationSetupHelper.GetPrimarySubscriptionDto(
            command.DataSubscriptions, command.SubscriptionAxis);

        try
        {
            var stopwatch = Stopwatch.StartNew();
            var ga = new GeneticAlgorithm(gaConfig);
            var fitnessFunction = new CompositeFitnessFunction(gaConfig);
            var rng = new Random();
            long totalEvals = 0;

            var trialTimeout = timeoutOptions.Value.BacktestTimeout;

            // Create initial population
            var population = ga.CreateInitialPopulation(activeAxes, rng);
            var bestFitness = double.MinValue;
            var stagnation = 0;

            // GA loop
            for (var gen = 0; gen < gaConfig.MaxGenerations; gen++)
            {
                ct.ThrowIfCancellationRequested();

                // Evaluate all individuals in parallel
                EvaluatePopulation(
                    population, command.StrategyName, command.BacktestSettings,
                    factory, fixedSubscriptions, dataCache,
                    fitnessFunction, filter, topTrials, failedTrials,
                    runId, startedAt, state,
                    maxParallelism, trialTimeout, ct);

                totalEvals += population.Count;
                generationsCompleted = gen + 1;

                // Update progress
                await progressCache.SetProgressAsync(
                    runId, totalEvals, gaConfig.MaxEvaluations, CancellationToken.None);

                // Track best fitness for stagnation detection
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
                    "GA {RunId} gen {Gen}: best={Best:F4}, stagnation={Stagnation}, evals={Evals}",
                    runId, gen, bestFitness, stagnation, totalEvals);

                // Check termination
                if (ga.ShouldTerminate(generationsCompleted, totalEvals, stagnation, stopwatch.Elapsed))
                    break;

                // Evolve next generation
                population = ga.Evolve(population, activeAxes, gen, stagnation, rng);
            }

            stopwatch.Stop();

            // Final progress
            await progressCache.SetProgressAsync(
                runId, totalEvals, gaConfig.MaxEvaluations, CancellationToken.None);

            var trials = topTrials.DrainSorted();
            var failedTrialDetails = failedTrials.Drain(runId);

            var record = new OptimizationRunRecord
            {
                Id = runId,
                StrategyName = command.StrategyName,
                StrategyVersion = state.StrategyVersion ?? "0",
                StartedAt = startedAt,
                CompletedAt = DateTimeOffset.UtcNow,
                DurationMs = (long)stopwatch.Elapsed.TotalMilliseconds,
                TotalCombinations = totalEvals,
                SortBy = command.SortBy,
                DataSubscription = primarySub,
                BacktestSettings = command.BacktestSettings,
                MaxParallelism = maxParallelism,
                Trials = trials,
                FailedTrialDetails = failedTrialDetails,
                FilteredTrials = Interlocked.Read(ref state.FilteredOutCount),
                FailedTrials = Interlocked.Read(ref state.FailedTrialCount),
                OptimizationMethod = "Genetic",
                GenerationsCompleted = generationsCompleted,
            };

            await helper.SaveOptimizationAsync(record);

            logger.LogInformation(
                "GA Optimization {RunId}: {Gens} generations, {Evals} evaluations, {Kept} kept, {Filtered} filtered, {Failed} failed in {Duration}ms",
                runId, generationsCompleted, totalEvals, trials.Count, state.FilteredOutCount, state.FailedTrialCount, stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("GA Optimization {RunId} was cancelled", runId);
            await helper.SaveErrorOptimizationAsync(
                command.StrategyName, command.BacktestSettings, primarySub,
                command.SortBy, maxParallelism,
                runId, startedAt, gaConfig.MaxEvaluations, topTrials,
                failedTrials, state.FilteredOutCount, state.FailedTrialCount,
                "Run was cancelled by user.",
                optimizationMethod: "Genetic", generationsCompleted: generationsCompleted);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GA Optimization {RunId} failed", runId);
            await helper.SaveErrorOptimizationAsync(
                command.StrategyName, command.BacktestSettings, primarySub,
                command.SortBy, maxParallelism,
                runId, startedAt, gaConfig.MaxEvaluations, topTrials,
                failedTrials, state.FilteredOutCount, state.FailedTrialCount,
                ex.Message, ex.StackTrace,
                optimizationMethod: "Genetic", generationsCompleted: generationsCompleted);
        }
        finally
        {
            await progressCache.RemoveProgressAsync(runId);
            cancellationRegistry.Remove(runId);
        }
    }

    /// <summary>
    /// Shared mutable state for cross-thread counters that can't use ref in lambdas.
    /// </summary>
    private sealed class EvalState
    {
        public string? StrategyVersion;
        public long FilteredOutCount;
        public long FailedTrialCount;
    }

    /// <summary>
    /// Evaluates all chromosomes in parallel using the same Partitioner pattern as brute-force.
    /// After evaluation, each chromosome's Fitness is set.
    /// </summary>
    private void EvaluatePopulation(
        List<Chromosome> population,
        string strategyName,
        BacktestSettingsDto settings,
        IOptimizationStrategyFactory factory,
        List<DataSubscription> fixedSubscriptions,
        Dictionary<string, (Asset Asset, TimeSeries<Int64Bar> Series)> dataCache,
        IFitnessFunction fitnessFunction,
        TrialFilter filter,
        BoundedTrialQueue topTrials,
        FailedTrialCollector failedTrials,
        Guid runId,
        DateTimeOffset startedAt,
        EvalState state,
        int maxParallelism,
        TimeSpan trialTimeout,
        CancellationToken ct)
    {
        // Convert chromosomes to combinations for evaluation
        var combos = new ParameterCombination[population.Count];
        for (var i = 0; i < population.Count; i++)
            combos[i] = ChromosomeFactory.ToParameterCombination(population[i]);

        var fitnesses = new double[population.Count];
        for (var i = 0; i < fitnesses.Length; i++)
            fitnesses[i] = double.MinValue;

        var actualTasks = Math.Min(maxParallelism, population.Count);
        var partitions = Partitioner.Create(
            Enumerable.Range(0, population.Count),
            EnumerablePartitionerOptions.NoBuffering)
            .GetPartitions(actualTasks);

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

                            if (!trialCts.TryReset())
                            {
                                trialCts.Dispose();
                                trialCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                            }
                            trialCts.CancelAfter(trialTimeout);

                            try
                            {
                                var record = helper.ExecuteTrial(
                                    strategyName, settings,
                                    combos[i], factory, fixedSubscriptions, dataCache,
                                    runId, startedAt, ref state.StrategyVersion, trialCts.Token);

                                fitnesses[i] = fitnessFunction.Evaluate(record.Metrics);

                                if (filter.Passes(record.Metrics))
                                    topTrials.TryAdd(record);
                                else
                                    Interlocked.Increment(ref state.FilteredOutCount);
                            }
                            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                            {
                                Interlocked.Increment(ref state.FailedTrialCount);
                                failedTrials.RecordTimeout(combos[i].Values, trialTimeout);
                            }
                            catch (OperationCanceledException)
                            {
                                throw;
                            }
                            catch (Exception ex)
                            {
                                Interlocked.Increment(ref state.FailedTrialCount);
                                failedTrials.Record(
                                    combos[i].Values,
                                    ex.GetType().FullName ?? ex.GetType().Name,
                                    ex.Message,
                                    ex.StackTrace ?? string.Empty);
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

        // Assign fitness scores back to chromosomes
        for (var i = 0; i < population.Count; i++)
            population[i].Fitness = fitnesses[i];
    }
}
