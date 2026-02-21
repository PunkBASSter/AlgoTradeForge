using System.Globalization;
using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Debug;
using AlgoTradeForge.WebApi.Contracts;

namespace AlgoTradeForge.WebApi.Endpoints;

/// <summary>
/// REST endpoints for debug session management (create, status, terminate).
/// Command/event transport has been migrated to WebSocket — see <see cref="DebugWebSocketHandler"/>.
/// The HTTP POST /commands endpoint is retained for backward compatibility but deprecated.
/// </summary>
public static class DebugEndpoints
{
    public static void MapDebugEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/debug-sessions")
            .WithTags("Debug");

        group.MapPost("/", StartSession)
            .WithName("StartDebugSession")
            .WithSummary("Start a debug backtest session (paused)")
            .WithOpenApi()
            .Produces<DebugSessionDto>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status400BadRequest);

        group.MapPost("/{id:guid}/commands", SendCommand)
            .WithName("SendDebugCommand")
            .WithSummary("[Deprecated — use WebSocket /api/debug-sessions/{id}/ws] Send a control command to a debug session")
            .WithOpenApi()
            .Produces<DebugStepResultDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);

        group.MapGet("/{id:guid}", GetStatus)
            .WithName("GetDebugSession")
            .WithSummary("Get debug session status")
            .WithOpenApi()
            .Produces<DebugSessionStatusDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);

        group.MapDelete("/{id:guid}", Terminate)
            .WithName("TerminateDebugSession")
            .WithSummary("Terminate and clean up a debug session")
            .WithOpenApi()
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> StartSession(
        StartDebugSessionRequest request,
        ICommandHandler<StartDebugSessionCommand, DebugSessionDto> handler,
        CancellationToken ct)
    {
        TimeSpan? timeFrame = null;
        if (request.TimeFrame is not null)
        {
            if (!TimeSpan.TryParse(request.TimeFrame, CultureInfo.InvariantCulture, out var parsed))
                return Results.BadRequest(new { error = $"Invalid TimeFrame '{request.TimeFrame}'." });
            timeFrame = parsed;
        }

        var command = new StartDebugSessionCommand
        {
            AssetName = request.AssetName,
            Exchange = request.Exchange,
            StrategyName = request.StrategyName,
            InitialCash = request.InitialCash,
            StartTime = request.StartTime,
            EndTime = request.EndTime,
            CommissionPerTrade = request.CommissionPerTrade,
            SlippageTicks = request.SlippageTicks,
            TimeFrame = timeFrame,
            StrategyParameters = request.StrategyParameters
        };

        try
        {
            var result = await handler.HandleAsync(command, ct);
            return Results.Created($"/api/debug-sessions/{result.SessionId}", result);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static async Task<IResult> SendCommand(
        Guid id,
        DebugCommandRequest request,
        ICommandHandler<SendDebugCommandRequest, DebugStepResultDto> handler,
        CancellationToken ct)
    {
        var (command, error) = ParseCommand(request);
        if (command is null)
            return Results.BadRequest(new { error });

        try
        {
            var result = await handler.HandleAsync(new SendDebugCommandRequest
            {
                SessionId = id,
                Command = command
            }, ct);
            return Results.Ok(result);
        }
        catch (ArgumentException ex)
        {
            return Results.NotFound(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Results.Conflict(new { error = ex.Message });
        }
    }

    private static IResult GetStatus(Guid id, IDebugSessionStore store)
    {
        var session = store.Get(id);
        if (session is null)
            return Results.NotFound(new { error = $"Debug session '{id}' not found." });

        var lastSnap = session.Probe.IsRunning || session.Probe.LastSnapshot.SequenceNumber > 0
            ? DebugStepResultDto.From(session.Probe.LastSnapshot, session.Probe.IsRunning)
            : null;

        return Results.Ok(new DebugSessionStatusDto(
            session.Id,
            session.Probe.IsRunning,
            lastSnap,
            session.CreatedAt));
    }

    private static async Task<IResult> Terminate(Guid id, IDebugSessionStore store)
    {
        if (!store.TryRemove(id, out var session))
            return Results.NotFound(new { error = $"Debug session '{id}' not found." });

        await session!.DisposeAsync();
        return Results.NoContent();
    }

    private static (DebugCommand? Command, string? Error) ParseCommand(DebugCommandRequest request)
    {
        return request.Command.ToLowerInvariant() switch
        {
            "continue" => (new DebugCommand.Continue(), null),
            "next" => (new DebugCommand.Next(), null),
            "next_bar" => (new DebugCommand.NextBar(), null),
            "next_trade" => (new DebugCommand.NextTrade(), null),
            "pause" => (new DebugCommand.Pause(), null),
            "run_to_sequence" when request.SequenceNumber.HasValue
                => (new DebugCommand.RunToSequence(request.SequenceNumber.Value), null),
            "run_to_sequence"
                => (null, "Command 'run_to_sequence' requires 'sequenceNumber' parameter."),
            "run_to_timestamp" when request.TimestampMs.HasValue
                => (new DebugCommand.RunToTimestamp(request.TimestampMs.Value), null),
            "run_to_timestamp"
                => (null, "Command 'run_to_timestamp' requires 'timestampMs' parameter."),
            _ => (null, $"Unknown command '{request.Command}'.")
        };
    }
}
