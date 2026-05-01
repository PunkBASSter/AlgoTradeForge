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

    // ---- Phase 2b: primary-bar analytical sidecar (TRD §9.4) -----------------
    //
    // Default-interface methods so existing IFeedContext impls (private-repo strategies,
    // null/test impls) keep compiling without modification. Phase 0's DIM audit (P0-1)
    // verified plugin-assembly dispatch under net10.0/AssemblyLoadContext.Default.

    /// <summary>
    /// Returns the latest sidecar row for the strategy's primary bar feed (e.g. EqI's
    /// <c>.flow</c> companion). Returns <c>false</c> when the primary has no sidecar
    /// or the sidecar has no data at the current bar's timestamp.
    /// </summary>
    /// <remarks>
    /// Lazy-loaded: a strategy that never calls this triggers zero loader hits (P2b-11).
    /// The returned array is a shared buffer — do NOT hold a reference across bars.
    /// Phase 4 will migrate this to <c>ReadOnlySpan&lt;double&gt;</c> alongside
    /// <see cref="TryGetLatest"/> (TRD §9.4).
    /// </remarks>
    bool TryGetPrimarySidecar(out double[] values)
    {
        values = [];
        return false;
    }

    /// <summary>
    /// Schema of the primary's sidecar (column names for index resolution at <c>OnInit</c>).
    /// <c>null</c> when the primary has no sidecar — strategies should branch on this once at
    /// init time and cache the column index, not re-resolve per bar.
    /// </summary>
    DataFeedSchema? PrimarySidecarSchema => null;

    /// <summary>
    /// Convenience accessor for the EqI sidecar's <c>signed_imbalance</c> column. Returns
    /// <see cref="double.NaN"/> when the primary has no sidecar, no row at the current ts,
    /// or the sidecar lacks a <c>signed_imbalance</c> column.
    /// </summary>
    double GetPrimarySignedImbalance()
    {
        var schema = PrimarySidecarSchema;
        if (schema is null) return double.NaN;
        var idx = IndexOf(schema.ColumnNames, "signed_imbalance");
        if (idx < 0) return double.NaN;
        return TryGetPrimarySidecar(out var values) && idx < values.Length
            ? values[idx]
            : double.NaN;
    }

    private static int IndexOf(string[] columns, string name)
    {
        for (var i = 0; i < columns.Length; i++)
            if (string.Equals(columns[i], name, StringComparison.Ordinal))
                return i;
        return -1;
    }
}
