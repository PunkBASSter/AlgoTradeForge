using System.Net;

namespace AlgoTradeForge.HistoryLoader.Application.Collection;

public static class NetworkErrorHelper
{
    /// <summary>
    /// Returns true for exceptions indicating network-level failures (DNS, connection refused, timeout)
    /// as opposed to server-side HTTP errors.
    /// </summary>
    public static bool IsNetworkError(Exception ex) => ex switch
    {
        // HttpRequestException with no StatusCode means the request never reached the server
        // (DNS failure, connection refused, TLS handshake failure, etc.)
        HttpRequestException { StatusCode: null } => true,

        // TaskCanceledException wrapping a TimeoutException means the request timed out
        TaskCanceledException { InnerException: TimeoutException } => true,

        _ => false
    };
}
