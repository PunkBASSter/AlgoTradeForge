using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.Domain;

public sealed record EquityAsset : Asset
{
    public override ISettlementCalculator SettlementCalculator => CashAndCarrySettlement.Instance;

    public override long ComputeAutoApplyDelta(AutoApplyType type, double rate, Position position, long lastPrice)
    {
        if (position.Quantity == 0m) return 0L;

        return type switch
        {
            AutoApplyType.Dividend => MoneyConvert.ToLong(position.Quantity * (decimal)rate),
            _ => 0L,
        };
    }
}
