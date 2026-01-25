namespace AlgoTradeForge.Domain.Trading;

public sealed class Portfolio
{
    public required decimal InitialCash { get; init; }
    public decimal Cash { get; private set; }
    public Position Position { get; } = new();

    public decimal Equity(decimal currentPrice) =>
        Cash + Position.Quantity * currentPrice;

    internal void Initialize()
    {
        Cash = InitialCash;
        Position.Reset();
    }

    internal void Apply(Fill fill)
    {
        var direction = fill.Side == OrderSide.Buy ? -1 : 1;
        var cashChange = fill.Price * fill.Quantity * direction - fill.Commission;
        Cash += cashChange;
        Position.Apply(fill);
    }
}
