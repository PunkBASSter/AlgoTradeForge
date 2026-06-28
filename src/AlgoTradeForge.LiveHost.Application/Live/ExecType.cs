namespace AlgoTradeForge.LiveHost.Application.Live;

// Venue-neutral execution-report kind. Venue connectors map their raw report type (e.g.
// BinanceExecutionReport.ExecutionType string, IB orderStatus/execDetails) onto this.
public enum ExecType
{
    New,
    Trade,
    Canceled,
    Expired,
    Rejected,
}
