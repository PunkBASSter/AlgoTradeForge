namespace AlgoTradeForge.Domain.Trading;

public sealed class Portfolio
{
    private readonly Dictionary<string, Position> _positions = new();

    public required long InitialCash { get; init; }
    public long Cash { get; private set; }
    public IReadOnlyDictionary<string, Position> Positions => _positions;

    public Position? GetPosition(string symbol) =>
        _positions.TryGetValue(symbol, out var position) ? position : null;

    public Position GetOrCreatePosition(Asset asset)
    {
        if (!_positions.TryGetValue(asset.Name, out var position))
        {
            position = new Position(asset);
            _positions[asset.Name] = position;
        }
        return position;
    }

    public long Equity(long currentPrice)
    {
        var positionValue = 0m;
        foreach (var position in _positions.Values)
        {
            positionValue += position.Quantity * currentPrice * position.Asset.Multiplier;
        }
        return Cash + (long)positionValue;
    }

    public long Equity(IReadOnlyDictionary<string, long> prices)
    {
        var positionValue = 0m;
        foreach (var (symbol, position) in _positions)
        {
            if (prices.TryGetValue(symbol, out var price))
            {
                positionValue += position.Quantity * price * position.Asset.Multiplier;
            }
        }
        return Cash + (long)positionValue;
    }

    internal void Initialize()
    {
        Cash = InitialCash;
        foreach (var position in _positions.Values)
        {
            position.Reset();
        }
        _positions.Clear();
    }

    internal void Apply(Fill fill)
    {
        var direction = fill.Side == OrderSide.Buy ? -1 : 1;
        var cashChange = (long)(fill.Price * fill.Quantity * fill.Asset.Multiplier * direction) - fill.Commission;
        Cash += cashChange;

        var position = GetOrCreatePosition(fill.Asset);
        position.Apply(fill);
    }
}
