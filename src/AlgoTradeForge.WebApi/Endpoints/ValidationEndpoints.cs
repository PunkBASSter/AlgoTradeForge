using System.Text.Json;
using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Persistence;
using AlgoTradeForge.Application.Progress;
using AlgoTradeForge.Application.Validation;
using AlgoTradeForge.WebApi.Contracts;

namespace AlgoTradeForge.WebApi.Endpoints;

public static class ValidationEndpoints
{
    public static void MapValidationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/validations")
            .WithTags("Validations");

        group.MapGet("/", ListValidations)
            .WithName("ListValidations")
            .WithSummary("List validation runs with optional filters")
            .WithOpenApi()
            .Produces<PagedResponse<ValidationRunSummaryResponse>>(StatusCodes.Status200OK);

        group.MapPost("/", RunValidation)
            .WithName("RunValidation")
            .WithSummary("Submit an overfitting validation for background execution")
            .WithOpenApi()
            .Produces<ValidationSubmissionResponse>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status400BadRequest);

        group.MapGet("/{id:guid}", GetValidation)
            .WithName("GetValidation")
            .WithSummary("Get a validation run with all stage results")
            .WithOpenApi()
            .Produces<ValidationRunResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/{id:guid}/status", GetValidationStatus)
            .WithName("GetValidationStatus")
            .WithSummary("Poll for validation progress")
            .WithOpenApi()
            .Produces<ValidationStatusResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/{id:guid}/cancel", CancelValidation)
            .WithName("CancelValidation")
            .WithSummary("Cancel an in-progress validation")
            .WithOpenApi()
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/{id:guid}/equity", GetValidationEquity)
            .WithName("GetValidationEquity")
            .WithSummary("Get equity curves and P&L deltas for surviving trials")
            .WithOpenApi()
            .Produces<ValidationEquityResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);

        group.MapDelete("/{id:guid}", DeleteValidation)
            .WithName("DeleteValidation")
            .WithSummary("Delete a validation run")
            .WithOpenApi()
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/{id:guid}/report", GetValidationReport)
            .WithName("GetValidationReport")
            .WithSummary("Download validation report as HTML")
            .WithOpenApi()
            .Produces(StatusCodes.Status200OK, contentType: "text/html")
            .Produces(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> ListValidations(
        IQueryHandler<ListValidationsQuery, PagedResult<ValidationRunRecord>> handler,
        string? strategyName,
        string? thresholdProfileName,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int limit = 50,
        int offset = 0,
        CancellationToken ct = default)
    {
        var filter = new ValidationRunQuery
        {
            StrategyName = strategyName,
            ThresholdProfileName = thresholdProfileName,
            From = from,
            To = to,
            Limit = limit,
            Offset = offset,
        };

        var paged = await handler.HandleAsync(new ListValidationsQuery(filter), ct);
        var items = paged.Items.Select(MapToSummary).ToList();
        var response = new PagedResponse<ValidationRunSummaryResponse>(
            items, paged.TotalCount, filter.Limit, filter.Offset,
            filter.Offset + items.Count < paged.TotalCount);
        return Results.Ok(response);
    }

    private static async Task<IResult> RunValidation(
        RunValidationRequest request,
        ICommandHandler<RunValidationCommand, ValidationSubmissionDto> handler,
        CancellationToken ct)
    {
        var command = new RunValidationCommand
        {
            OptimizationRunId = request.OptimizationRunId,
            ThresholdProfileName = request.ThresholdProfileName,
        };

        try
        {
            // CancellationToken.None: handler does synchronous setup (placeholder insertion,
            // progress registration) then launches background work with its own 30-min timeout.
            // Client disconnect must not abort the setup phase.
            var submission = await handler.HandleAsync(command, CancellationToken.None);
            var response = new ValidationSubmissionResponse
            {
                Id = submission.Id,
                CandidateCount = submission.CandidateCount,
            };
            return Results.Accepted($"/api/validations/{submission.Id}/status", response);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static async Task<IResult> GetValidation(
        Guid id,
        IQueryHandler<GetValidationByIdQuery, ValidationRunRecord?> handler,
        CancellationToken ct)
    {
        var record = await handler.HandleAsync(new GetValidationByIdQuery(id), ct);
        if (record is null)
            return Results.NotFound(new { error = $"Validation with ID '{id}' not found." });

        return Results.Ok(MapToResponse(record));
    }

    private static async Task<IResult> GetValidationStatus(
        Guid id,
        IQueryHandler<GetValidationStatusQuery, ValidationStatusDto?> handler,
        CancellationToken ct)
    {
        var dto = await handler.HandleAsync(new GetValidationStatusQuery(id), ct);
        if (dto is null)
            return Results.NotFound(new { error = $"Validation '{id}' not found." });

        return Results.Ok(new ValidationStatusResponse
        {
            Id = dto.Id,
            Status = dto.Status,
            CurrentStage = dto.CurrentStage,
            TotalStages = dto.TotalStages,
            Result = dto.Result is not null ? MapToResponse(dto.Result) : null,
        });
    }

    private static async Task<IResult> CancelValidation(
        Guid id,
        ICommandHandler<CancelRunCommand, bool> handler,
        CancellationToken ct)
    {
        var cancelled = await handler.HandleAsync(new CancelRunCommand(id), ct);
        if (!cancelled)
            return Results.NotFound(new { error = $"Run '{id}' not found." });

        return Results.Ok(new { id, status = "Cancelled" });
    }

    private static async Task<IResult> GetValidationEquity(
        Guid id,
        IQueryHandler<GetValidationEquityQuery, ValidationEquityDto?> handler,
        CancellationToken ct)
    {
        var dto = await handler.HandleAsync(new GetValidationEquityQuery(id), ct);
        if (dto is null)
            return Results.NotFound(new { error = $"Validation with ID '{id}' not found." });

        var response = new ValidationEquityResponse
        {
            Trials = dto.Trials.Select(t => new TrialEquityResponse
            {
                TrialIndex = t.TrialIndex,
                TrialId = t.TrialId,
                Timestamps = t.Timestamps,
                Equity = t.Equity,
                PnlDeltas = t.PnlDeltas,
            }).ToList(),
            InitialEquity = dto.InitialEquity,
        };

        return Results.Ok(response);
    }

    private static async Task<IResult> DeleteValidation(
        Guid id,
        IRunCancellationRegistry cancellationRegistry,
        IValidationRepository repository,
        CancellationToken ct)
    {
        cancellationRegistry.TryCancel(id);

        var deleted = await repository.DeleteAsync(id, ct);
        if (!deleted)
            return Results.NotFound(new { error = $"Validation with ID '{id}' not found." });

        return Results.NoContent();
    }

    private static async Task<IResult> GetValidationReport(
        Guid id,
        IValidationRepository repository,
        CancellationToken ct)
    {
        var record = await repository.GetByIdAsync(id, ct);
        if (record is null)
            return Results.NotFound(new { error = $"Validation with ID '{id}' not found." });

        var html = ValidationReportGenerator.GenerateHtml(record);
        return Results.Content(html, "text/html", System.Text.Encoding.UTF8);
    }

    private static ValidationRunSummaryResponse MapToSummary(ValidationRunRecord r) => new()
    {
        Id = r.Id,
        StrategyName = r.StrategyName,
        StrategyVersion = r.StrategyVersion,
        ThresholdProfileName = r.ThresholdProfileName,
        StartedAt = r.StartedAt,
        CompletedAt = r.CompletedAt,
        DurationMs = r.DurationMs,
        Status = r.Status,
        CandidatesIn = r.CandidatesIn,
        CandidatesOut = r.CandidatesOut,
        CompositeScore = r.CompositeScore,
        Verdict = r.Verdict,
        VerdictSummary = r.VerdictSummary,
        InvocationCount = r.InvocationCount,
        CategoryScores = DeserializeJsonDict(r.CategoryScoresJson),
    };

    private static ValidationRunResponse MapToResponse(ValidationRunRecord r) => new()
    {
        Id = r.Id,
        OptimizationRunId = r.OptimizationRunId,
        StrategyName = r.StrategyName,
        StrategyVersion = r.StrategyVersion,
        StartedAt = r.StartedAt,
        CompletedAt = r.CompletedAt,
        DurationMs = r.DurationMs,
        Status = r.Status,
        ThresholdProfileName = r.ThresholdProfileName,
        CandidatesIn = r.CandidatesIn,
        CandidatesOut = r.CandidatesOut,
        CompositeScore = r.CompositeScore,
        Verdict = r.Verdict,
        VerdictSummary = r.VerdictSummary,
        Rejections = DeserializeJsonList(r.RejectionsJson),
        CategoryScores = DeserializeJsonDict(r.CategoryScoresJson),
        InvocationCount = r.InvocationCount,
        ErrorMessage = r.ErrorMessage,
        StageResults = r.StageResults.Select(s => new StageResultResponse
        {
            StageNumber = s.StageNumber,
            StageName = s.StageName,
            CandidatesIn = s.CandidatesIn,
            CandidatesOut = s.CandidatesOut,
            DurationMs = s.DurationMs,
            DetailJson = s.CandidateVerdictsJson,
        }).ToList(),
    };

    private static readonly JsonSerializerOptions CamelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static IReadOnlyList<string> DeserializeJsonList(string? json)
    {
        if (string.IsNullOrEmpty(json)) return [];
        return JsonSerializer.Deserialize<List<string>>(json, CamelCase) ?? [];
    }

    private static IReadOnlyDictionary<string, double> DeserializeJsonDict(string? json)
    {
        if (string.IsNullOrEmpty(json)) return new Dictionary<string, double>();
        return JsonSerializer.Deserialize<Dictionary<string, double>>(json, CamelCase)
            ?? new Dictionary<string, double>();
    }
}
