namespace AlgoTradeForge.Domain.History;

public static class TimeSeriesExtensions
{
    public static TimeSeries<Int64Bar> Resample(this TimeSeries<Int64Bar> source, TimeSpan targetStep)
    {
        var sourceStep = source.Step;

        if (targetStep <= sourceStep)
            throw new ArgumentException(
                $"Target step ({targetStep}) must be greater than source step ({sourceStep}).",
                nameof(targetStep));

        if (targetStep.Ticks % sourceStep.Ticks != 0)
            throw new ArgumentException(
                $"Target step ({targetStep}) must be an exact multiple of source step ({sourceStep}).",
                nameof(targetStep));

        if (source.Count == 0)
            return new TimeSeries<Int64Bar>(source.StartTime, targetStep);

        var ratio = (int)(targetStep.Ticks / sourceStep.Ticks);
        var result = new TimeSeries<Int64Bar>(source.StartTime, targetStep);

        for (var i = 0; i < source.Count; i += ratio)
        {
            var groupEnd = Math.Min(i + ratio, source.Count);
            var first = source[i];

            var open = first.Open;
            var high = first.High;
            var low = first.Low;
            var close = first.Close;
            var volume = first.Volume;

            for (var j = i + 1; j < groupEnd; j++)
            {
                var bar = source[j];
                if (bar.High > high) high = bar.High;
                if (bar.Low < low) low = bar.Low;
                close = bar.Close;
                volume += bar.Volume;
            }

            result.Add(new Int64Bar(open, high, low, close, volume));
        }

        return result;
    }
}
