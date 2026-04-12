namespace AlgoTradeForge.Domain.Events;

public sealed record FilterEvaluationEvent(
    DateTimeOffset Timestamp,
    string Source,
    string AssetName,
    Dictionary<string, int> FilterScores,
    int CompositeScore,
    bool Passed) : IBacktestEvent
{
    public static string TypeId => "filter.eval";
    public static ExportMode DefaultExportMode => ExportMode.Backtest | ExportMode.Live;
}
