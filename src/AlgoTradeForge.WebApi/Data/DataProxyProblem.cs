using Microsoft.AspNetCore.Mvc;

namespace AlgoTradeForge.WebApi.Data;

/// <summary>
/// Translates upstream HistoryLoader 5xx / connection failures into <see cref="ProblemDetails"/>
/// with stable <c>code</c> values. Upstream 4xx is forwarded byte-identical so domain-meaningful
/// body shapes reach the FE unchanged. The FE matches on <c>code</c>, never title/detail.
/// </summary>
public static class DataProxyProblem
{
    public static IResult Unavailable(string detail) => Problem(
        statusCode: StatusCodes.Status502BadGateway,
        code: "history_loader_unavailable",
        title: "History loader is unreachable",
        detail: detail);

    public static IResult Timeout(string detail) => Problem(
        statusCode: StatusCodes.Status504GatewayTimeout,
        code: "upstream_timeout",
        title: "History loader did not respond in time",
        detail: detail);

    public static IResult UpstreamError(int upstreamStatus, string detail) => Problem(
        // Pass through upstream 5xx (e.g. 503) instead of fixing 502 so clients that branch
        // on status see the original code.
        statusCode: upstreamStatus,
        code: "upstream_error",
        title: "History loader returned an error",
        detail: detail);

    private static IResult Problem(int statusCode, string code, string title, string detail) =>
        Results.Problem(
            statusCode: statusCode,
            title: title,
            detail: detail,
            // Stable code on extensions; title is human-readable and may change.
            extensions: new Dictionary<string, object?> { ["code"] = code });
}
