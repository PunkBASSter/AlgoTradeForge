using AlgoTradeForge.HistoryLoader.Domain;

namespace AlgoTradeForge.HistoryLoader.Application.Abstractions;

public interface ICandleWriter
{
    void Write(string assetDir, string interval, KlineRecord record, int decimalDigits);
    long? ResumeFrom(string assetDir, string interval);
}
