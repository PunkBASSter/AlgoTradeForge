namespace AlgoTradeForge.Domain.Events;

public sealed record ExitEvaluationEvent(
    DateTimeOffset Timestamp,
    string Source,
    string AssetName,
    long GroupId,
    Dictionary<string, int> RuleScores,
    int CompositeScore,
    bool ExitTriggered) : IBacktestEvent
{
    public static string TypeId => "exit.eval";
    public static ExportMode DefaultExportMode => ExportMode.Backtest | ExportMode.Live;
}
