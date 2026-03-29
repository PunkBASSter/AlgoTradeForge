using System.Diagnostics;
using System.Text.Json;
using AlgoTradeForge.Application.Persistence;
using AlgoTradeForge.Domain.Validation;
using AlgoTradeForge.Domain.Validation.Stages;

namespace AlgoTradeForge.Application.Validation;

public sealed class ValidationPipeline
{
    public static int StageCount => Stages.Count;

    private static readonly IReadOnlyList<IValidationStage> Stages =
    [
        new PreFlightStage(),               // 0
        new BasicProfitabilityStage(),      // 1
        new StatisticalSignificanceStage(), // 2
        new ParameterLandscapeStage(),      // 3 (stub)
        new WalkForwardOptimizationStage(), // 4 (stub)
        new WalkForwardMatrixStage(),       // 5 (stub)
        new MonteCarloPermutationStage(),   // 6 (stub)
        new SelectionBiasAuditStage(),      // 7 (stub)
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public (List<StageResultRecord> Results, IReadOnlyList<int> Survivors) Execute(
        SimulationCache cache,
        IReadOnlyList<TrialSummary> trials,
        ValidationThresholdProfile profile,
        Guid validationRunId,
        Action<int, int>? onProgress,
        CancellationToken ct)
    {
        var context = new ValidationContext
        {
            Cache = cache,
            Trials = trials,
            Profile = profile,
            ActiveCandidateIndices = Enumerable.Range(0, trials.Count).ToList(),
        };

        var stageResults = new List<StageResultRecord>(Stages.Count);

        for (var i = 0; i < Stages.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            onProgress?.Invoke(i, Stages.Count);

            var stage = Stages[i];
            var candidatesIn = context.ActiveCandidateIndices.Count;

            if (candidatesIn == 0) break;

            var sw = Stopwatch.StartNew();
            var result = stage.Execute(context, ct);
            sw.Stop();

            context.ActiveCandidateIndices = result.SurvivingIndices;

            stageResults.Add(new StageResultRecord
            {
                ValidationRunId = validationRunId,
                StageNumber = stage.StageNumber,
                StageName = stage.StageName,
                CandidatesIn = candidatesIn,
                CandidatesOut = result.SurvivingIndices.Count,
                DurationMs = (long)sw.Elapsed.TotalMilliseconds,
                CandidateVerdictsJson = JsonSerializer.Serialize(result.Verdicts, JsonOptions),
            });
        }

        onProgress?.Invoke(Stages.Count, Stages.Count);
        return (stageResults, context.ActiveCandidateIndices);
    }
}
