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
        for (var i = 0; i < columns.Length; i++)
        {
            if (columns[i].Length != timestamps.Length)
                throw new ArgumentException(
                    $"Column {i} has length {columns[i].Length} but timestamp array has length {timestamps.Length}.");
        }

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
