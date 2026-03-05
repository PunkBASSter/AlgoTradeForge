namespace AlgoTradeForge.Domain.Trading;

public enum OrderStatus
{
    Pending,
    PartiallyFilled,
    Filled,
    Rejected,
    Triggered,
    Cancelled
}
