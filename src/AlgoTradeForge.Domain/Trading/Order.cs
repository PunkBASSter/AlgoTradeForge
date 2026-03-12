namespace AlgoTradeForge.Domain.Trading;

public sealed class Order
{
    public required long Id { get; set; }
    public required Asset Asset { get; init; }
    public required OrderSide Side { get; init; }
    public required OrderType Type { get; init; }
    public required decimal Quantity { get; init; }
    public long? LimitPrice { get; init; }
    public long? StopPrice { get; init; }
    public long? StopLossPrice { get; init; }
    public IReadOnlyList<TakeProfitLevel>? TakeProfitLevels { get; init; }
    public long? GroupId { get; init; }
    // Backing int field for thread-safe reads/writes across live trading threads.
    // Volatile ensures acquire/release semantics so cross-thread Status mutations
    // are immediately visible without requiring a full lock.
    private int _status = (int)OrderStatus.Pending;
    public OrderStatus Status
    {
        get => (OrderStatus)Volatile.Read(ref _status);
        internal set => Volatile.Write(ref _status, (int)value);
    }
    public DateTimeOffset SubmittedAt { get; internal set; }
}
