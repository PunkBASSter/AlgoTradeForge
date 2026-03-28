namespace AlgoTradeForge.Domain.Engine;

public static class TimeFrameFormatter
{
    public static string Format(TimeSpan timeFrame) => timeFrame.TotalSeconds switch
    {
        < 60 => $"{(int)timeFrame.TotalSeconds}s",
        < 3600 => $"{(int)timeFrame.TotalMinutes}m",
        < 86400 => $"{(int)timeFrame.TotalHours}h",
        _ => $"{(int)timeFrame.TotalDays}d"
    };

    /// <summary>Inverse of <see cref="Format"/>: parses shorthand like "1h", "15m", "30s", "1d".</summary>
    public static bool TryParseShorthand(string? input, out TimeSpan result)
    {
        result = default;
        if (string.IsNullOrEmpty(input) || input.Length < 2)
            return false;

        var suffix = input[^1];
        if (!int.TryParse(input[..^1], out var value) || value <= 0)
            return false;

        result = suffix switch
        {
            's' => TimeSpan.FromSeconds(value),
            'm' => TimeSpan.FromMinutes(value),
            'h' => TimeSpan.FromHours(value),
            'd' => TimeSpan.FromDays(value),
            _ => default,
        };
        return suffix is 's' or 'm' or 'h' or 'd';
    }
}
