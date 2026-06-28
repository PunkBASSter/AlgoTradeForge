using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.LiveHost.Application.Live;

// Venue-neutral inbound execution report fed to LiveSessionDispatcher.OnExecutionReport.
// Prices/quantities are in market units; the dispatcher scales via new ScaleContext(Asset).
public sealed record ExecutionReport(
    long OrderId,
    Asset Asset,
    OrderSide Side,
    ExecType ExecType,
    decimal LastFillPrice,
    decimal LastFillQty,
    decimal Commission,
    OrderStatus Status);
