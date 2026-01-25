namespace AlgoTradeForge.Domain.Trading;

public sealed record Fill(
    long OrderId,
    DateTimeOffset Timestamp,
    decimal Price,
    decimal Quantity,
    OrderSide Side,
    decimal Commission);
