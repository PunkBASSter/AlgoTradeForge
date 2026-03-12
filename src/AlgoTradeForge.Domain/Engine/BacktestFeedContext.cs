using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy;

namespace AlgoTradeForge.Domain.Engine;

/// <summary>
/// Backtest implementation of <see cref="IFeedContext"/>. Holds pre-loaded <see cref="FeedSeries"/>
/// with cursors that advance chronologically. Reuses <c>double[]</c> row buffers (zero allocation in hot loop).
/// </summary>
public sealed class BacktestFeedContext : IFeedContext
{
    private readonly Dictionary<string, FeedEntry> _feeds = [];

    public void Register(string feedKey, DataFeedSchema schema, FeedSeries series, Asset? asset = null)
    {
        _feeds[feedKey] = new FeedEntry(schema, series, new double[schema.ColumnCount], asset);
    }

    /// <summary>
    /// Advances all feed cursors to the given timestamp. Called by the engine before each bar delivery.
    /// Marks feeds as HasNewData if any records were consumed this step.
    /// </summary>
    public void AdvanceTo(long timestampMs)
    {
        foreach (var entry in _feeds.Values)
        {
            entry.HasNew = false;
            while (entry.Cursor < entry.Series.Count &&
                   entry.Series.GetTimestamp(entry.Cursor) <= timestampMs)
            {
                entry.Series.GetRow(entry.Cursor, entry.RowBuffer);
                entry.Cursor++;
                entry.HasNew = true;
                entry.HasData = true;
            }
        }
    }

    public void Reset()
    {
        foreach (var entry in _feeds.Values)
        {
            entry.Cursor = 0;
            entry.HasNew = false;
            entry.HasData = false;
        }
    }

    public bool TryGetLatest(string feedKey, out double[] values)
    {
        if (_feeds.TryGetValue(feedKey, out var entry) && entry.HasData)
        {
            values = entry.RowBuffer;
            return true;
        }

        values = [];
        return false;
    }

    public bool HasNewData(string feedKey) =>
        _feeds.TryGetValue(feedKey, out var entry) && entry.HasNew;

    public DataFeedSchema GetSchema(string feedKey) =>
        _feeds.TryGetValue(feedKey, out var entry)
            ? entry.Schema
            : throw new InvalidOperationException($"No feed '{feedKey}' registered.");

    /// <summary>Returns all feeds with auto-apply configuration that have new data.</summary>
    public IEnumerable<(string FeedKey, DataFeedSchema Schema, double[] Values)> GetAutoApplyFeeds()
    {
        foreach (var (key, entry) in _feeds)
            if (entry.Schema.AutoApply is not null && entry.HasNew)
                yield return (key, entry.Schema, entry.RowBuffer);
    }

    /// <summary>
    /// Returns all feeds with auto-apply configuration (regardless of HasNew state).
    /// Used at startup to pre-resolve column indices and asset bindings.
    /// </summary>
    public IEnumerable<(string FeedKey, DataFeedSchema Schema, Asset Asset)> GetAutoApplyConfigs()
    {
        foreach (var (key, entry) in _feeds)
            if (entry.Schema.AutoApply is not null && entry.Asset is not null)
                yield return (key, entry.Schema, entry.Asset);
    }

    internal sealed class FeedEntry(DataFeedSchema schema, FeedSeries series, double[] rowBuffer, Asset? asset)
    {
        public readonly DataFeedSchema Schema = schema;
        public readonly FeedSeries Series = series;
        public readonly double[] RowBuffer = rowBuffer;
        public readonly Asset? Asset = asset;
        public int Cursor;
        public bool HasNew;
        public bool HasData;
    }
}
