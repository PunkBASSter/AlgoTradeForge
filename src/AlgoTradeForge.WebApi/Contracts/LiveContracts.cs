using AlgoTradeForge.Application;
using AlgoTradeForge.Domain.Live;

namespace AlgoTradeForge.WebApi.Contracts;

public sealed record StartLiveSessionRequest
{
    public required string StrategyName { get; init; }
    public required decimal InitialCash { get; init; }
    public Dictionary<string, object>? StrategyParameters { get; init; }
    public List<DataSubscriptionDto>? DataSubscriptions { get; init; }
    public string[]? EnabledEvents { get; init; }
    public string AccountName { get; init; } = "paper";
}

public sealed record LiveSessionSubmissionResponse
{
    public required Guid SessionId { get; init; }
}

public sealed record LiveSessionStatusResponse
{
    public required Guid SessionId { get; init; }
    public required string Status { get; init; }
    public required string StrategyName { get; init; }
    public required string StrategyVersion { get; init; }
    public required string Exchange { get; init; }
    public required string AssetName { get; init; }
    public required string AccountName { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
}

public sealed record LiveSessionListResponse
{
    public required IReadOnlyList<LiveSessionStatusResponse> Sessions { get; init; }
}

public sealed record LiveSessionDataResponse
{
    public required IReadOnlyList<CandleResponse> Candles { get; init; }
    public required IReadOnlyList<FillResponse> Fills { get; init; }
    public required IReadOnlyList<PendingOrderResponse> PendingOrders { get; init; }
    public required AccountResponse Account { get; init; }
    public required string TimeFrame { get; init; }
    public required IReadOnlyList<LastBarResponse> LastBars { get; init; }
    public required IReadOnlyList<ExchangeTradeResponse> ExchangeTrades { get; init; }
}

public sealed record CandleResponse(long Time, decimal Open, decimal High, decimal Low, decimal Close, long Volume);
public sealed record FillResponse(long OrderId, string Timestamp, decimal Price, decimal Quantity, string Side, decimal Commission);
public sealed record PendingOrderResponse(long Id, string Side, string Type, decimal Quantity, decimal? LimitPrice, decimal? StopPrice);
public sealed record AccountResponse(decimal InitialCash, decimal Cash, decimal ExchangeBalance, IReadOnlyList<PositionResponse> Positions);
public sealed record PositionResponse(string Symbol, decimal Quantity, decimal AverageEntryPrice, decimal RealizedPnl);
public sealed record ExchangeTradeResponse(long OrderId, string Timestamp, decimal Price, decimal Quantity, string Side, decimal Commission, string CommissionAsset);
public sealed record LastBarResponse(string Symbol, string TimeFrame, long Time, decimal Open, decimal High, decimal Low, decimal Close, long Volume);
