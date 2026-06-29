using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.LiveHost.Application.Live;

// Venue-neutral inbound execution report fed to LiveSessionDispatcher.OnExecutionReport. Symbol is
// informational only — the dispatcher resolves the session-authoritative Asset (and its scale) from
// the order→session map. Prices/quantities are in market units.
public sealed record ExecutionReport(
    long OrderId,
    string Symbol,
    OrderSide Side,
    ExecType ExecType,
    decimal LastFillPrice,
    decimal LastFillQty,
    decimal Commission,
    OrderStatus Status,
    DateTimeOffset TransactionTime,
    OrderType Type,
    decimal OriginalQuantity);
