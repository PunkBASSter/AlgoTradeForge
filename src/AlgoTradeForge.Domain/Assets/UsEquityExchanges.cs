namespace AlgoTradeForge.Domain;

/// <summary>
/// US cash-equity exchanges — the single source of truth for the crypto-vs-equity venue
/// decision, shared by the catalog classifier (HistoryLoader.Domain) and authoritative
/// asset resolution (Infrastructure). Add a venue here once; both consumers pick it up.
/// </summary>
public static class UsEquityExchanges
{
    private static readonly HashSet<string> Names =
        new(StringComparer.OrdinalIgnoreCase) { "NASDAQ", "NYSE", "NYSEMKT", "AMEX", "ARCA", "BATS" };

    public static bool Contains(string exchange) => Names.Contains(exchange);
}
