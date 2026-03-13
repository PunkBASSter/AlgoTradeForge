using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.Domain.Strategy;

/// <summary>
/// Read-only pull interface for auxiliary data. Updated by the engine before each
/// <c>OnBarStart</c>. Strategy queries data during <c>OnBarComplete</c> — same
/// experience in backtest and live.
/// </summary>
public interface IFeedContext
{
    /// <summary>
    /// Returns the latest record at or before the current bar's timestamp.
    /// The returned array is a shared buffer — do NOT hold a reference across bars.
    /// </summary>
    bool TryGetLatest(string feedKey, out double[] values);

    /// <summary>True if a new record arrived at or before the current bar's timestamp.</summary>
    bool HasNewData(string feedKey);

    /// <summary>Access the feed schema (column names) for index resolution during OnInit.</summary>
    DataFeedSchema GetSchema(string feedKey);
}
