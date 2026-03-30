using System.Text.Json;
using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Persistence;
using AlgoTradeForge.Domain.Validation.Stages;

namespace AlgoTradeForge.Application.Validation;

public sealed record GetValidationEquityQuery(Guid ValidationId) : IQuery<ValidationEquityDto?>;

/// <summary>
/// Loads surviving trials' equity curves from the source optimization,
/// reconstructs per-bar P&amp;L deltas, and returns the data for chart rendering.
/// </summary>
public sealed class GetValidationEquityQueryHandler(
    IValidationRepository validationRepository,
    IRunRepository runRepository) : IQueryHandler<GetValidationEquityQuery, ValidationEquityDto?>
{
    private const int MaxTrials = 10;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public async Task<ValidationEquityDto?> HandleAsync(GetValidationEquityQuery query, CancellationToken ct = default)
    {
        var validation = await validationRepository.GetByIdAsync(query.ValidationId, ct);
        if (validation is null) return null;

        var optimization = await runRepository.GetOptimizationByIdAsync(validation.OptimizationRunId, ct);
        if (optimization is null) return null;

        // Find surviving trial IDs from the last stage's verdicts
        var survivorIds = ExtractSurvivorIds(validation.StageResults);

        // Match survivors to optimization trials
        var trialMap = new Dictionary<Guid, (int Index, BacktestRunRecord Trial)>();
        for (var i = 0; i < optimization.Trials.Count; i++)
        {
            var trial = optimization.Trials[i];
            if (trial.EquityCurve.Count > 0)
                trialMap[trial.Id] = (i, trial);
        }

        var results = new List<TrialEquityDto>();
        foreach (var sid in survivorIds.Take(MaxTrials))
        {
            if (!trialMap.TryGetValue(sid, out var entry)) continue;
            var (index, trial) = entry;

            var curve = trial.EquityCurve;
            var timestamps = new long[curve.Count];
            var equity = new double[curve.Count];
            var deltas = new double[curve.Count];

            var initialCapital = (double)trial.Metrics.InitialCapital;

            for (var i = 0; i < curve.Count; i++)
            {
                timestamps[i] = curve[i].TimestampMs;
                equity[i] = curve[i].Value;
                deltas[i] = i == 0
                    ? curve[i].Value - initialCapital
                    : curve[i].Value - curve[i - 1].Value;
            }

            results.Add(new TrialEquityDto(index, trial.Id, timestamps, equity, deltas));
        }

        var initialEquity = optimization.Trials.Count > 0
            ? (double)optimization.Trials[0].Metrics.InitialCapital
            : 0.0;

        return new ValidationEquityDto(results, initialEquity);
    }

    private static List<Guid> ExtractSurvivorIds(IReadOnlyList<StageResultRecord> stageResults)
    {
        // Walk stages in reverse to find the last stage with verdicts,
        // then extract the trial IDs that passed
        for (var i = stageResults.Count - 1; i >= 0; i--)
        {
            var sr = stageResults[i];
            if (string.IsNullOrEmpty(sr.CandidateVerdictsJson)) continue;

            var verdicts = JsonSerializer.Deserialize<List<CandidateVerdict>>(sr.CandidateVerdictsJson, JsonOptions);
            if (verdicts is null || verdicts.Count == 0) continue;

            return verdicts
                .Where(v => v.Passed)
                .Select(v => v.TrialId)
                .ToList();
        }

        return [];
    }
}

public sealed record ValidationEquityDto(
    IReadOnlyList<TrialEquityDto> Trials,
    double InitialEquity);

public sealed record TrialEquityDto(
    int TrialIndex,
    Guid TrialId,
    long[] Timestamps,
    double[] Equity,
    double[] PnlDeltas);
