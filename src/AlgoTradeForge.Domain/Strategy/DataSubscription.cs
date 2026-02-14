namespace AlgoTradeForge.Domain.Strategy;

public record DataSubscription(Asset Asset, TimeSpan TimeFrame, bool IsExportable = false);