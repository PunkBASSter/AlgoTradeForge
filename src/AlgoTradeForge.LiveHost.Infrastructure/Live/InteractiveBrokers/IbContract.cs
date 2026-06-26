namespace AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

// Configured tier: round-trips through session config; value equality makes it the resolver cache key.
// Exchange is the IB routing destination (STK -> "SMART"; FUT -> the futures exchange, e.g. "COMEX").
// PrimaryExch is the listing exchange for stocks (e.g. "NASDAQ") and empty for futures.
internal sealed record IbContract(
    string Symbol,
    IbSecType SecType,
    string Exchange,
    string PrimaryExch,
    string Currency);
