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
        var positionValue = 0L;
        foreach (var position in _positions.Values)
        {
            positionValue += position.Asset.GetSettlementCalculator().ComputePositionValue(position, currentPrice);
        }
        return Cash + positionValue;
    }

    public long Equity(IReadOnlyDictionary<string, long> prices)
    {
        var positionValue = 0L;
        foreach (var (symbol, position) in _positions)
        {
            if (prices.TryGetValue(symbol, out var price))
            {
                positionValue += position.Asset.GetSettlementCalculator().ComputePositionValue(position, price);
            }
        }
        return Cash + positionValue;
    }

    /// <summary>
    /// Computes the total initial margin used by all margin-settled positions.
    /// Based on entry price (not current price) — margin requirement is locked at position open.
    /// </summary>
    public long ComputeUsedMargin()
    {
        var margin = 0L;
        foreach (var position in _positions.Values)
        {
            if (position.Asset is not IMarginAsset marginAsset) continue;
            if (position.Quantity == 0m) continue;
            var marginReq = marginAsset.MarginRequirement ?? 1.0m;
            margin += MoneyConvert.ToLong(
                Math.Abs(position.Quantity) * (decimal)position.AverageEntryPrice
                * position.Asset.Multiplier * marginReq);
        }
        return margin;
    }

    public long AvailableMargin(long currentPrice) =>
        Equity(currentPrice) - ComputeUsedMargin();

    public long AvailableMargin(IReadOnlyDictionary<string, long> prices) =>
        Equity(prices) - ComputeUsedMargin();

    internal void Initialize()
    {
        Cash = InitialCash;
        foreach (var position in _positions.Values)
        {
            position.Reset();
        }
        _positions.Clear();
    }

    internal void ApplyCashAdjustment(long delta) => Cash += delta;

    internal void Apply(Fill fill)
    {
        var position = GetOrCreatePosition(fill.Asset);
        var fillRealizedPnl = position.Apply(fill);
        Cash += fill.Asset.GetSettlementCalculator().ComputeCashDelta(fill, fillRealizedPnl);
    }
}
