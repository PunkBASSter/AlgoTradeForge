namespace AlgoTradeForge.Domain.Trading;

public sealed class Position
{
    public Asset Asset { get; }
    public decimal Quantity { get; private set; }
    public decimal AverageEntryPrice { get; private set; }
    public decimal RealizedPnl { get; private set; }

    public Position(Asset asset, decimal quantity = 0m, decimal averageEntryPrice = 0m, decimal realizedPnl = 0m)
    {
        Asset = asset;
        Quantity = quantity;
        AverageEntryPrice = averageEntryPrice;
        RealizedPnl = realizedPnl;
    }

    public decimal UnrealizedPnl(decimal currentPrice) =>
        Quantity == 0m ? 0m : (currentPrice - AverageEntryPrice) * Quantity * Asset.Multiplier;

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
                RealizedPnl += closedQuantity * (fill.Price - AverageEntryPrice) * Asset.Multiplier;
            }
        }
        else
        {
            RealizedPnl += Quantity * (fill.Price - AverageEntryPrice) * Asset.Multiplier;
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
