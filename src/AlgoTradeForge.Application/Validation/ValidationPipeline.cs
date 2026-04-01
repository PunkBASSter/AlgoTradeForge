using System.Diagnostics;
using System.Text.Json;
using AlgoTradeForge.Application.Persistence;
using AlgoTradeForge.Domain.Validation;
using AlgoTradeForge.Domain.Validation.Stages;

namespace AlgoTradeForge.Application.Validation;

public sealed class ValidationPipeline
{
    public static int StageCount => DefaultStages.Count;

    private static readonly IReadOnlyList<IValidationStage> DefaultStages =
    [
        new PreFlightStage(),               // 0
        new BasicProfitabilityStage(),      // 1
        new StatisticalSignificanceStage(), // 2
        new ParameterLandscapeStage(),      // 3
        new WalkForwardOptimizationStage(), // 4
        new WalkForwardMatrixStage(),       // 5
        new MonteCarloPnlDeltasPermutationStage(),   // 6
        new SelectionBiasAuditStage(),      // 7
    ];

    private readonly IReadOnlyList<IValidationStage> _stages;

    public ValidationPipeline() => _stages = DefaultStages;

    internal ValidationPipeline(IReadOnlyList<IValidationStage> stages) => _stages = stages;

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
        CancellationToken ct,
        long totalCombinations = 0)
    {
        var allIndices = Enumerable.Range(0, trials.Count).ToList();
        var context = new ValidationContext
        {
            Cache = cache,
            Trials = trials,
            Profile = profile,
            AllCandidateIndices = allIndices,
            TotalCombinations = totalCombinations,
        };

        var stageResults = new List<StageResultRecord>(_stages.Count);
        var rawResults = new List<StageResult>(_stages.Count);

        for (var i = 0; i < _stages.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            onProgress?.Invoke(i, _stages.Count);

            var stage = _stages[i];

            var sw = Stopwatch.StartNew();
            var result = stage.Execute(context, ct);
            sw.Stop();

            rawResults.Add(result);

            stageResults.Add(new StageResultRecord
            {
                ValidationRunId = validationRunId,
                StageNumber = stage.StageNumber,
                StageName = stage.StageName,
                CandidatesIn = allIndices.Count,
                CandidatesOut = result.SurvivingIndices.Count,
                DurationMs = (long)sw.Elapsed.TotalMilliseconds,
                CandidateVerdictsJson = JsonSerializer.Serialize(result.Verdicts, JsonOptions),
            });
        }

        // Final survivors = intersection of all stages' surviving indices
        HashSet<int>? survivors = null;
        foreach (var result in rawResults)
        {
            if (survivors is null)
                survivors = [.. result.SurvivingIndices];
            else
                survivors.IntersectWith(result.SurvivingIndices);
        }

        var finalSurvivors = survivors?.OrderBy(x => x).ToList()
            ?? (IReadOnlyList<int>)[];

        onProgress?.Invoke(_stages.Count, _stages.Count);
        return (stageResults, finalSurvivors);
    }
}
