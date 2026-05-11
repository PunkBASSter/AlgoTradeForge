namespace AlgoTradeForge.WebApi.Data;

/// <summary>Configuration for the HistoryLoader proxy client; bound from <c>"HistoryLoader"</c>.</summary>
public sealed class HistoryLoaderOptions
{
    public string BaseUrl { get; init; } = "http://localhost:5050";

    /// <summary>Per-request timeout for non-SSE calls. SSE streams override to infinite.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
}
