namespace AlgoTradeForge.HistoryLoader.Application.Abstractions;

public sealed record AutoApplySpec(string Type, string RateColumn, string? SignConvention = null);

public interface ISchemaManager
{
    void EnsureSchema(string assetDir, string feedName, string interval, string[] columns, AutoApplySpec? autoApply = null);
    void EnsureCandleConfig(string assetDir, int decimalDigits, string interval);
}
