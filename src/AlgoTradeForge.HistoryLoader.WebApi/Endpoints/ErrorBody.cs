namespace AlgoTradeForge.HistoryLoader.WebApi.Endpoints;

/// <summary>Canonical wire shape for all error responses: {code, message}.</summary>
public sealed record ErrorBody(string Code, string Message);
