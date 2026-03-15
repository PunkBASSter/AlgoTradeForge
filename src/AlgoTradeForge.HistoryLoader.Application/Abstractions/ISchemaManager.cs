namespace AlgoTradeForge.HistoryLoader.Application.Abstractions;

public interface ISchemaManager
{
    void EnsureSchema(string assetDir, string feedName, string interval, string[] columns, AutoApplySpec? autoApply = null);
    void EnsureCandleConfig(string assetDir, int decimalDigits, string interval);
}
