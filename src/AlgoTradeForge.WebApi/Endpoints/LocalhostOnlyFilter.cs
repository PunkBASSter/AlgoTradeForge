using System.Net;

namespace AlgoTradeForge.WebApi.Endpoints;

/// <summary>
/// Endpoint filter that rejects non-loopback connections with 403 Forbidden.
/// Applied to debug endpoints which should only be accessible from localhost.
/// </summary>
public sealed class LocalhostOnlyFilter : IEndpointFilter
{
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var remoteIp = context.HttpContext.Connection.RemoteIpAddress;
        if (remoteIp is not null && !IPAddress.IsLoopback(remoteIp))
        {
            return ValueTask.FromResult<object?>(Results.StatusCode(StatusCodes.Status403Forbidden));
        }

        return next(context);
    }
}
