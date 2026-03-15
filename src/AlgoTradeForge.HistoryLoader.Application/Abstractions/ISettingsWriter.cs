namespace AlgoTradeForge.HistoryLoader.Application.Abstractions;

/// <summary>
/// Persists discovered configuration values back to the settings file
/// so future runs can skip probing.
/// </summary>
public interface ISettingsWriter
{
    void UpdateFeedHistoryStart(string symbol, string assetType,
        string feedName, string feedInterval, DateOnly historyStart);
}
