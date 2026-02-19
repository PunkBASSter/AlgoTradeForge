namespace AlgoTradeForge.Domain.Trading;

public sealed record Fill(
    long OrderId,
    Asset Asset,
    DateTimeOffset Timestamp,
    long Price,
    decimal Quantity,
    OrderSide Side,
    long Commission);
