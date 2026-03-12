namespace AlgoTradeForge.Domain.Trading;

public sealed class Position
{
    public Asset Asset { get; }
    public decimal Quantity { get; private set; }
    public long AverageEntryPrice { get; private set; }
    public long RealizedPnl { get; private set; }

    public Position(Asset asset, decimal quantity = 0m, long averageEntryPrice = 0L, long realizedPnl = 0L)
    {
        Asset = asset;
        Quantity = quantity;
        AverageEntryPrice = averageEntryPrice;
        RealizedPnl = realizedPnl;
    }

    public long UnrealizedPnl(long currentPrice) =>
        Quantity == 0m ? 0L : MoneyConvert.ToLong((currentPrice - AverageEntryPrice) * Quantity * Asset.Multiplier);

    internal long Apply(Fill fill)
    {
        var direction = fill.Side == OrderSide.Buy ? 1 : -1;
        var fillQuantity = fill.Quantity * direction;
        var newQuantity = Quantity + fillQuantity;
        var realizedFromFill = 0L;

        if (Quantity == 0m)
        {
            AverageEntryPrice = fill.Price;
        }
        else if (Math.Sign(newQuantity) == Math.Sign(Quantity))
        {
            if (Math.Abs(newQuantity) > Math.Abs(Quantity))
            {
                var totalCost = Quantity * AverageEntryPrice + fillQuantity * fill.Price;
                AverageEntryPrice = MoneyConvert.ToLong(totalCost / newQuantity);
            }
            else
            {
                var closedQuantity = Quantity - newQuantity;
                realizedFromFill = MoneyConvert.ToLong(closedQuantity * (fill.Price - AverageEntryPrice) * Asset.Multiplier);
                RealizedPnl += realizedFromFill;
            }
        }
        else
        {
            realizedFromFill = MoneyConvert.ToLong(Quantity * (fill.Price - AverageEntryPrice) * Asset.Multiplier);
            RealizedPnl += realizedFromFill;
            AverageEntryPrice = fill.Price;
        }

        Quantity = newQuantity;
        return realizedFromFill;
    }

    internal void Reset()
    {
        Quantity = 0m;
        AverageEntryPrice = 0L;
        RealizedPnl = 0L;
    }
}
