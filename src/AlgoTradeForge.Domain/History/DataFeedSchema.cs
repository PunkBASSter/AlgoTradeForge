namespace AlgoTradeForge.Domain.History;

/// <summary>
/// Describes the columns of a non-OHLCV data feed. Column indices are resolved once
/// at startup; the hot loop delivers <c>double[]</c> rows with zero dictionary lookups.
/// </summary>
public sealed record DataFeedSchema(string FeedKey, string[] ColumnNames, AutoApplyConfig? AutoApply = null)
{
    public int ColumnCount => ColumnNames.Length;

    /// <summary>
    /// Resolves a column name to its index in the <c>double[]</c> row buffer.
    /// Call once in <c>OnInit()</c> and cache the result.
    /// </summary>
    public int GetColumnIndex(string columnName)
    {
        for (var i = 0; i < ColumnNames.Length; i++)
            if (ColumnNames[i] == columnName)
                return i;

        throw new ArgumentException(
            $"Column '{columnName}' not found in feed '{FeedKey}'. Available: {string.Join(", ", ColumnNames)}");
    }
}
