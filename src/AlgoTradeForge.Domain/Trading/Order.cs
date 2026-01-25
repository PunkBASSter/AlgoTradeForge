namespace AlgoTradeForge.Domain.Trading;

public sealed class Order
{
    public required long Id { get; init; }
    public required Asset Asset { get; init; }
    public required OrderSide Side { get; init; }
    public required OrderType Type { get; init; }
    public required decimal Quantity { get; init; }
    public decimal? LimitPrice { get; init; }
    public OrderStatus Status { get; internal set; } = OrderStatus.Pending;
    public DateTimeOffset SubmittedAt { get; internal set; }
}
