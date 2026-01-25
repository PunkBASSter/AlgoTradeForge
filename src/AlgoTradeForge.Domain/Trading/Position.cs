namespace AlgoTradeForge.Domain.Trading;

public sealed class Position
{
    public decimal Quantity { get; private set; }
    public decimal AverageEntryPrice { get; private set; }
    public decimal RealizedPnl { get; private set; }

    public decimal UnrealizedPnl(decimal currentPrice) =>
        Quantity == 0m ? 0m : (currentPrice - AverageEntryPrice) * Quantity;

    internal void Apply(Fill fill)
    {
        var direction = fill.Side == OrderSide.Buy ? 1 : -1;
        var fillQuantity = fill.Quantity * direction;
        var newQuantity = Quantity + fillQuantity;

        if (Quantity == 0m)
        {
            AverageEntryPrice = fill.Price;
        }
        else if (Math.Sign(newQuantity) == Math.Sign(Quantity))
        {
            if (Math.Abs(newQuantity) > Math.Abs(Quantity))
            {
                var totalCost = Quantity * AverageEntryPrice + fillQuantity * fill.Price;
                AverageEntryPrice = totalCost / newQuantity;
            }
            else
            {
                var closedQuantity = Quantity - newQuantity;
                RealizedPnl += closedQuantity * (fill.Price - AverageEntryPrice);
            }
        }
        else
        {
            RealizedPnl += Quantity * (fill.Price - AverageEntryPrice);
            AverageEntryPrice = fill.Price;
        }

        Quantity = newQuantity;
    }

    internal void Reset()
    {
        Quantity = 0m;
        AverageEntryPrice = 0m;
        RealizedPnl = 0m;
    }
}
