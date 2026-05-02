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

    // Phase 2b — primary bar feed's sidecar (e.g. EqI's <feedId>.flow). The loader is held
    // in a closure and invoked at most once on first access. Until then, we own the schema
    // (read out of feeds.json at engine setup) but pay no I/O.
    private DataFeedSchema? _primarySidecarSchema;
    private string? _primarySidecarFeedKey;
    private Func<FeedSeries?>? _primarySidecarLoader;
    private FeedEntry? _primarySidecarEntry;
    private bool _primarySidecarLoadAttempted;

    // Latest ts the engine has advanced to. Buffers AdvanceTo calls until lazy materialization
    // so a sidecar accessed mid-run catches up to "now" instead of starting at the first bar.
    private long _latestAdvanceTs = long.MinValue;

    public void Register(string feedKey, DataFeedSchema schema, FeedSeries series, Asset? asset = null)
    {
        _feeds[feedKey] = new FeedEntry(schema, series, new double[schema.ColumnCount], asset);
    }

    /// <summary>
    /// Phase 2b — registers the primary bar feed's analytical sidecar with a deferred loader
    /// (TRD §9.4). The series isn't loaded until a strategy calls
    /// <see cref="IFeedContext.TryGetPrimarySidecar"/> or accesses
    /// <see cref="IFeedContext.PrimarySidecarSchema"/> in a way that requires the data —
    /// strategies that ignore the sidecar pay zero loader cost (P2b-11).
    /// </summary>
    /// <param name="feedKey">Sidecar feed-id (e.g. <c>"EqI_ticks_500000.flow"</c>).</param>
    /// <param name="schema">Pre-resolved schema; column indices are usable at OnInit time without forcing a load.</param>
    /// <param name="seriesLoader">
    /// Returns the loaded <see cref="FeedSeries"/> on first call; <c>null</c> when the on-disk
    /// sidecar is empty or missing despite the manifest pointer (P2b-12 surfaces that case as
    /// an engine-init error rather than silent <c>NaN</c> at runtime).
    /// </param>
    public void RegisterPrimarySidecarLazy(
        string feedKey,
        DataFeedSchema schema,
        Func<FeedSeries?> seriesLoader)
    {
        ArgumentException.ThrowIfNullOrEmpty(feedKey);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(seriesLoader);

        _primarySidecarFeedKey = feedKey;
        _primarySidecarSchema = schema;
        _primarySidecarLoader = seriesLoader;
        _primarySidecarLoadAttempted = false;
        _primarySidecarEntry = null;
    }

    /// <summary>
    /// Advances all feed cursors to the given timestamp. Called by the engine before each bar delivery.
    /// Marks feeds as HasNewData if any records were consumed this step.
    /// </summary>
    public void AdvanceTo(long timestampMs)
    {
        _latestAdvanceTs = timestampMs;

        foreach (var entry in _feeds.Values)
            AdvanceEntry(entry, timestampMs);

        // Sidecar tracks the primary bar's ts the same way side feeds do — but only if the
        // strategy has materialized it (TryGetPrimarySidecar called at least once). Strategies
        // that don't touch it stay zero-cost.
        if (_primarySidecarEntry is not null)
            AdvanceEntry(_primarySidecarEntry, timestampMs);
    }

    public void Reset()
    {
        _latestAdvanceTs = long.MinValue;

        foreach (var entry in _feeds.Values)
        {
            entry.Cursor = 0;
            entry.HasNew = false;
            entry.HasData = false;
        }

        if (_primarySidecarEntry is not null)
        {
            _primarySidecarEntry.Cursor = 0;
            _primarySidecarEntry.HasNew = false;
            _primarySidecarEntry.HasData = false;
        }
    }

    private static void AdvanceEntry(FeedEntry entry, long timestampMs)
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

    public bool TryGetLatest(string feedKey, out ReadOnlySpan<double> values)
    {
        if (_feeds.TryGetValue(feedKey, out var entry) && entry.HasData)
        {
            values = entry.RowBuffer;
            return true;
        }

        values = ReadOnlySpan<double>.Empty;
        return false;
    }

    public bool HasNewData(string feedKey) =>
        _feeds.TryGetValue(feedKey, out var entry) && entry.HasNew;

    public DataFeedSchema GetSchema(string feedKey) =>
        _feeds.TryGetValue(feedKey, out var entry)
            ? entry.Schema
            : throw new InvalidOperationException($"No feed '{feedKey}' registered.");

    // ---- Phase 2b: primary sidecar overrides --------------------------------

    /// <summary>
    /// Schema is known up-front (read out of feeds.json at engine setup) so strategies can
    /// resolve column indices in <c>OnInit</c> without forcing the loader to run.
    /// </summary>
    public DataFeedSchema? PrimarySidecarSchema => _primarySidecarSchema;

    public bool TryGetPrimarySidecar(out ReadOnlySpan<double> values)
    {
        // Lazy materialization: the loader runs at most once. After that, the sidecar entry
        // behaves like any other registered feed (cursor advances via AdvanceTo, HasData flips).
        EnsurePrimarySidecarMaterialized();

        if (_primarySidecarEntry is { HasData: true } entry)
        {
            values = entry.RowBuffer;
            return true;
        }

        values = ReadOnlySpan<double>.Empty;
        return false;
    }

    private void EnsurePrimarySidecarMaterialized()
    {
        if (_primarySidecarLoadAttempted) return;
        if (_primarySidecarLoader is null || _primarySidecarSchema is null) return;

        _primarySidecarLoadAttempted = true;

        var series = _primarySidecarLoader.Invoke();
        if (series is null)
        {
            // Manifest pointed at this sidecar but no data on disk — surface loudly at first
            // strategy access rather than silently returning NaN forever (P2b-12).
            throw new InvalidOperationException(
                $"Primary sidecar '{_primarySidecarFeedKey}' is registered in feeds.json but its " +
                "data could not be loaded (no partitions matched, or all partitions empty). " +
                "Re-aggregate the parent feed to repopulate the sidecar.");
        }

        _primarySidecarEntry = new FeedEntry(
            _primarySidecarSchema,
            series,
            new double[_primarySidecarSchema.ColumnCount],
            asset: null);

        // Catch up to the engine's latest known timestamp. If the strategy first asks for the
        // sidecar at bar 100, the cursor needs to skip rows 0..99 so reads are anchored to "now"
        // — otherwise the strategy would see ancient values for the next 100 bars.
        if (_latestAdvanceTs != long.MinValue)
            AdvanceEntry(_primarySidecarEntry, _latestAdvanceTs);
    }

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
