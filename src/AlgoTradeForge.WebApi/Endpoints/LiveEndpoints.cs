using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Live;
using AlgoTradeForge.Application.Optimization;
using AlgoTradeForge.Domain.Live;
using AlgoTradeForge.WebApi.Contracts;

namespace AlgoTradeForge.WebApi.Endpoints;

public static class LiveEndpoints
{
    public static void MapLiveEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/live/sessions")
            .WithTags("Live Trading");

        group.MapPost("/", StartSession)
            .WithName("StartLiveSession")
            .WithSummary("Start a live or paper trading session")
            .WithOpenApi()
            .Produces<LiveSessionSubmissionResponse>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status400BadRequest);

        group.MapGet("/{id:guid}", GetSession)
            .WithName("GetLiveSession")
            .WithSummary("Get live session status")
            .WithOpenApi()
            .Produces<LiveSessionStatusResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);

        group.MapDelete("/{id:guid}", StopSession)
            .WithName("StopLiveSession")
            .WithSummary("Stop a live trading session")
            .WithOpenApi()
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/", ListSessions)
            .WithName("ListLiveSessions")
            .WithSummary("List active live trading sessions")
            .WithOpenApi()
            .Produces<LiveSessionListResponse>(StatusCodes.Status200OK);
    }

    private static async Task<IResult> StartSession(
        StartLiveSessionRequest request,
        ICommandHandler<StartLiveSessionCommand, LiveSessionSubmissionDto> handler,
        CancellationToken ct)
    {
        var routing = ParseRouting(request.EnabledEvents);

        var command = new StartLiveSessionCommand
        {
            StrategyName = request.StrategyName,
            InitialCash = request.InitialCash,
            StrategyParameters = request.StrategyParameters,
            DataSubscriptions = request.DataSubscriptions,
            Routing = routing,
            AccountName = request.AccountName,
        };

        try
        {
            var result = await handler.HandleAsync(command, ct);
            var response = new LiveSessionSubmissionResponse { SessionId = result.SessionId };
            return Results.Accepted($"/api/live/sessions/{result.SessionId}", response);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static Task<IResult> GetSession(
        Guid id,
        ILiveSessionStore store)
    {
        var entry = store.Get(id);
        if (entry is null)
            return Task.FromResult(Results.NotFound(new { error = $"Live session '{id}' not found." }));

        var response = new LiveSessionStatusResponse
        {
            SessionId = id,
            Status = entry.Connector.Status.ToString(),
            StrategyName = entry.StrategyName,
            StrategyVersion = entry.StrategyVersion,
            Exchange = entry.Exchange,
            AssetName = entry.AssetName,
            AccountName = entry.AccountName,
            StartedAt = entry.StartedAt,
        };
        return Task.FromResult(Results.Ok(response));
    }

    private static async Task<IResult> StopSession(
        Guid id,
        ICommandHandler<StopLiveSessionCommand, bool> handler,
        CancellationToken ct)
    {
        var stopped = await handler.HandleAsync(new StopLiveSessionCommand(id), ct);
        if (!stopped)
            return Results.NotFound(new { error = $"Live session '{id}' not found." });

        return Results.Ok(new { id, status = "Stopped" });
    }

    private static Task<IResult> ListSessions(ILiveSessionStore store)
    {
        var sessionIds = store.GetActiveSessionIds();
        var sessions = sessionIds.Select(id =>
        {
            var entry = store.Get(id);
            return new LiveSessionStatusResponse
            {
                SessionId = id,
                Status = entry?.Connector.Status.ToString() ?? "Unknown",
                StrategyName = entry?.StrategyName ?? "Unknown",
                StrategyVersion = entry?.StrategyVersion ?? "Unknown",
                Exchange = entry?.Exchange ?? "Unknown",
                AssetName = entry?.AssetName ?? "Unknown",
                AccountName = entry?.AccountName ?? "Unknown",
                StartedAt = entry?.StartedAt ?? DateTimeOffset.MinValue,
            };
        }).ToList();

        var response = new LiveSessionListResponse { Sessions = sessions };
        return Task.FromResult(Results.Ok(response));
    }

    private static LiveEventRouting ParseRouting(string[]? enabledEvents)
    {
        if (enabledEvents is null || enabledEvents.Length == 0)
            return LiveEventRouting.OnBarComplete | LiveEventRouting.OnTrade;

        var routing = LiveEventRouting.None;
        foreach (var evt in enabledEvents)
        {
            if (Enum.TryParse<LiveEventRouting>(evt, ignoreCase: true, out var flag))
                routing |= flag;
        }
        return routing == LiveEventRouting.None
            ? LiveEventRouting.OnBarComplete | LiveEventRouting.OnTrade
            : routing;
    }
}
