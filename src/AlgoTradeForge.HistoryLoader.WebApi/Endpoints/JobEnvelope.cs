using System.Text.Json;
using AlgoTradeForge.HistoryLoader.Application.Index;

namespace AlgoTradeForge.HistoryLoader.WebApi.Endpoints;

public sealed record JobEnvelope(
    string JobId, string Kind, string State, string? FeedKey,
    string? CreatedAt, string? UpdatedAt, JobError? Error, JobProgress? Progress)
{
    private static readonly JsonSerializerOptions SnakeCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static JobEnvelope From(IndexJobRow row)
    {
        JobError? error = row.Error is not null
            ? JsonSerializer.Deserialize<JobError>(row.Error, SnakeCase)
            : null;

        // '{}' is the sentinel written by CreateJob; skip parsing to leave Progress null.
        JobProgress? progress = !string.IsNullOrEmpty(row.ProgressJson) && row.ProgressJson != "{}"
            ? JsonSerializer.Deserialize<JobProgress>(row.ProgressJson, SnakeCase)
            : null;

        return new JobEnvelope(
            JobId: row.Id,
            Kind: row.Kind,
            State: row.State,
            FeedKey: row.FeedKey,
            CreatedAt: null,
            UpdatedAt: null,
            Error: error,
            Progress: progress);
    }
}

public sealed record JobError(string Code, string Message);

// Detail stays a raw JsonElement — its inner keys are already snake_case in storage and pass through untouched.
public sealed record JobProgress(string? Phase, int Done, int Total, JsonElement? Detail);
