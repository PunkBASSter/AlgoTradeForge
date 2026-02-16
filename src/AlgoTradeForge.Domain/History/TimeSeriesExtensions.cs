namespace AlgoTradeForge.Domain.History;

public static class TimeSeriesExtensions
{
    /// <summary>
    /// Resamples bars by grouping on aligned timestamp boundaries.
    /// Gap in source produces no bar in output.
    /// </summary>
    public static TimeSeries<Int64Bar> Resample(this IReadOnlyList<Int64Bar> source, TimeSpan targetStep)
    {
        var targetMs = (long)targetStep.TotalMilliseconds;
        if (targetMs <= 0)
            throw new ArgumentException("Target step must be positive.", nameof(targetStep));

        if (source.Count == 0)
            return new TimeSeries<Int64Bar>();

        // Infer source step from first two bars (if available) for validation
        if (source.Count >= 2)
        {
            var sourceStepMs = source[1].TimestampMs - source[0].TimestampMs;
            if (targetMs <= sourceStepMs)
                throw new ArgumentException(
                    $"Target step ({targetStep}) must be greater than source step ({TimeSpan.FromMilliseconds(sourceStepMs)}).",
                    nameof(targetStep));
        }

        var result = new TimeSeries<Int64Bar>();

        var groupKey = AlignDown(source[0].TimestampMs, targetMs);
        var open = source[0].Open;
        var high = source[0].High;
        var low = source[0].Low;
        var close = source[0].Close;
        var volume = source[0].Volume;
        var groupTimestampMs = source[0].TimestampMs;

        for (var i = 1; i < source.Count; i++)
        {
            var bar = source[i];
            var barGroupKey = AlignDown(bar.TimestampMs, targetMs);

            if (barGroupKey != groupKey)
            {
                // Flush previous group
                result.Add(new Int64Bar(groupTimestampMs, open, high, low, close, volume));

                // Start new group
                groupKey = barGroupKey;
                open = bar.Open;
                high = bar.High;
                low = bar.Low;
                close = bar.Close;
                volume = bar.Volume;
                groupTimestampMs = bar.TimestampMs;
            }
            else
            {
                if (bar.High > high) high = bar.High;
                if (bar.Low < low) low = bar.Low;
                close = bar.Close;
                volume += bar.Volume;
            }
        }

        // Flush last group
        result.Add(new Int64Bar(groupTimestampMs, open, high, low, close, volume));

        return result;
    }

    /// <summary>
    /// Returns bars in [fromMs, toMs) using binary search for O(log n) start/end.
    /// </summary>
    public static TimeSeries<Int64Bar> Slice(this IReadOnlyList<Int64Bar> source, long fromMs, long toMs)
    {
        if (fromMs >= toMs || source.Count == 0)
            return new TimeSeries<Int64Bar>();

        var startIndex = LowerBound(source, fromMs);
        var endIndex = LowerBound(source, toMs);

        if (startIndex >= endIndex)
            return new TimeSeries<Int64Bar>();

        var result = new TimeSeries<Int64Bar>(endIndex - startIndex);
        for (var i = startIndex; i < endIndex; i++)
            result.Add(source[i]);

        return result;
    }

    private static long AlignDown(long timestampMs, long stepMs)
        => timestampMs - timestampMs % stepMs;

    /// <summary>
    /// Returns the index of the first bar with TimestampMs >= target.
    /// </summary>
    private static int LowerBound(IReadOnlyList<Int64Bar> source, long targetMs)
    {
        var lo = 0;
        var hi = source.Count;
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (source[mid].TimestampMs < targetMs)
                lo = mid + 1;
            else
                hi = mid;
        }
        return lo;
    }
}
