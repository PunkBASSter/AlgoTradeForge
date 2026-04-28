namespace AlgoTradeForge.Domain.Strategy.Modules;

public interface ICrossAssetContext
{
    double ZScore { get; set; }
    double HedgeRatio { get; set; }
    bool IsCointegrated { get; set; }
}
