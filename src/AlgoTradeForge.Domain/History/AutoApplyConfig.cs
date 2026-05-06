namespace AlgoTradeForge.Domain.History;

public enum AutoApplyType { FundingRate, MarkToMarket, Dividend, SwapRate }

/// <summary>Per-feed auto-apply config. Cap/Floor are realized-rate bounds (FundingRate clips at Cap).</summary>
public sealed record AutoApplyConfig(
    AutoApplyType Type,
    string RateColumn,
    string? SignConvention = null,
    double? Cap = null,
    double? Floor = null,
    int? IntervalHours = null,
    bool? Disclaimer = null);
