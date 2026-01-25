namespace AlgoTradeForge.Domain.Trading;

public sealed record Fill(
    long OrderId,
    Asset Asset,
    DateTimeOffset Timestamp,
    decimal Price,
    decimal Quantity,
    OrderSide Side,
    decimal Commission);
