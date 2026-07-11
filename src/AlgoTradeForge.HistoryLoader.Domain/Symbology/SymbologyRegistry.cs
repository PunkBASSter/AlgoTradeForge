namespace AlgoTradeForge.HistoryLoader.Domain.Symbology;

public sealed class SymbologyRegistry(IEnumerable<IExchangeSymbology> symbologies)
{
    private readonly Dictionary<string, IExchangeSymbology> _map =
        symbologies.ToDictionary(s => s.Exchange, StringComparer.OrdinalIgnoreCase);

    public IExchangeSymbology? Get(string exchange) =>
        _map.TryGetValue(exchange, out var s) ? s : null;
}
