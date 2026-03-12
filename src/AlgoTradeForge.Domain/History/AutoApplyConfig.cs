namespace AlgoTradeForge.Domain.History;

public enum AutoApplyType { FundingRate, MarkToMarket, Dividend, SwapRate }

public sealed record AutoApplyConfig(AutoApplyType Type, string RateColumn, string? SignConvention = null);
