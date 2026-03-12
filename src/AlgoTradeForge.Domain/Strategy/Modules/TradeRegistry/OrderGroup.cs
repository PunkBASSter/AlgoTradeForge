using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.Domain.Strategy.Modules.TradeRegistry;

public record struct TpLevel
{
    public required long Price { get; init; }
    public required decimal ClosurePercentage { get; init; }
    public long OrderId { get; internal set; }
}

public sealed class OrderGroup
{
    public required long GroupId { get; init; }
    public OrderGroupStatus Status { get; internal set; } = OrderGroupStatus.PendingEntry;

    // Entry
    public long EntryOrderId { get; internal set; }
    public required OrderSide EntrySide { get; init; }
    public required decimal EntryQuantity { get; init; }
    public long EntryPrice { get; internal set; }

    // Stop-loss
    public long SlOrderId { get; internal set; }
    public long SlPrice { get; internal set; }

    // Take-profit
    public TpLevel[] TpLevels { get; internal set; } = [];
    public int FilledTpCount { get; internal set; }

    // Liquidation
    public long LiquidationOrderId { get; internal set; }

    // Tracking
    public decimal RemainingQuantity { get; internal set; }
    public long RealizedPnl { get; internal set; }
    public required Asset Asset { get; init; }
    public DateTimeOffset CreatedAt { get; internal set; }
    public DateTimeOffset? ClosedAt { get; internal set; }

    // Debug
    public string? Tag { get; init; }
}
