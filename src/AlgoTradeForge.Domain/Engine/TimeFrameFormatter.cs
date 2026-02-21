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
}
