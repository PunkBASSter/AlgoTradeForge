namespace AlgoTradeForge.WebApi.Middleware;

/// <summary>
/// Catches <see cref="OperationCanceledException"/> thrown when a client disconnects mid-request.
/// Prevents DeveloperExceptionPage from surfacing expected client-abort behavior as errors.
/// </summary>
public sealed class ClientDisconnectMiddleware(RequestDelegate next, ILogger<ClientDisconnectMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            logger.LogDebug("Request cancelled by client: {Method} {Path}",
                context.Request.Method, context.Request.Path);

            if (!context.Response.HasStarted)
                context.Response.StatusCode = 499; // nginx convention: Client Closed Request
        }
    }
}
