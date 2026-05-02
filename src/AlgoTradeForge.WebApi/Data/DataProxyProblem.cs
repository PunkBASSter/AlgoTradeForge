using Microsoft.AspNetCore.Mvc;

namespace AlgoTradeForge.WebApi.Data;

/// <summary>
/// Translates upstream HistoryLoader failures into <see cref="ProblemDetails"/> with stable
/// <c>code</c> values (Phase 3 / P3-6). Only 5xx and connection failures are translated;
/// upstream 4xx (422, 423, 409) is forwarded byte-identical because those status codes carry
/// domain-meaningful body shapes the FE already differentiates.
/// </summary>
/// <remarks>
/// Stable codes: <c>history_loader_unavailable</c> (502), <c>upstream_timeout</c> (504),
/// <c>upstream_error</c> (passthrough 5xx). The FE matches on <c>code</c>, never on the
/// title or detail.
/// </remarks>
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
        // Pass through the upstream 5xx status (e.g. 503 from queue-full passthrough), not
        // a fixed 502 — preserves the upstream contract for clients that branch on status.
        statusCode: upstreamStatus,
        code: "upstream_error",
        title: "History loader returned an error",
        detail: detail);

    private static IResult Problem(int statusCode, string code, string title, string detail) =>
        Results.Problem(
            statusCode: statusCode,
            title: title,
            detail: detail,
            // Stable error code on extensions, not the title — title is human-readable
            // and could change without breaking FE contract.
            extensions: new Dictionary<string, object?> { ["code"] = code });
}
