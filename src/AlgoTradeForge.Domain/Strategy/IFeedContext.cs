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
    /// The returned span aliases a shared row buffer — the type system enforces
    /// "do not hold across bars" since <see cref="ReadOnlySpan{T}"/> is a <c>ref struct</c>
    /// and cannot be stored in fields, captured by closures, or boxed (TRD §9.4).
    /// </summary>
    bool TryGetLatest(string feedKey, out ReadOnlySpan<double> values);

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
    /// The returned span aliases a shared row buffer; the ref-struct lifetime prevents the
    /// caller from holding it across bars (TRD §9.4 — Phase 4 P4-9 lock).
    /// </remarks>
    bool TryGetPrimarySidecar(out ReadOnlySpan<double> values)
    {
        values = ReadOnlySpan<double>.Empty;
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
        return TryGetPrimarySidecar(out ReadOnlySpan<double> values) && idx < values.Length
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
