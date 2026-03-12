namespace AlgoTradeForge.Domain.History;

/// <summary>
/// Columnar time series for auxiliary data (funding rates, OI, taker volume, etc.).
/// Column-major layout: <c>Columns[colIndex][rowIndex]</c> for cache-friendly sequential reads.
/// All values are <c>double</c> — natural units, no scaling factors.
/// </summary>
public sealed class FeedSeries
{
    public long[] Timestamps { get; }
    public double[][] Columns { get; }
    public int Count { get; }
    public int ColumnCount => Columns.Length;

    public FeedSeries(long[] timestamps, double[][] columns)
    {
        if (columns.Length > 0 && columns[0].Length != timestamps.Length)
            throw new ArgumentException("All columns must have the same length as the timestamp array.");

        Timestamps = timestamps;
        Columns = columns;
        Count = timestamps.Length;
    }

    public long GetTimestamp(int index) => Timestamps[index];

    /// <summary>
    /// Fills a pre-allocated buffer with all column values at the given row index.
    /// Zero-allocation in the hot loop.
    /// </summary>
    public void GetRow(int index, double[] buffer)
    {
        for (var c = 0; c < Columns.Length; c++)
            buffer[c] = Columns[c][index];
    }
}
