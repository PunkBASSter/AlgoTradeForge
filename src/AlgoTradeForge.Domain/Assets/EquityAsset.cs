using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.Domain;

public sealed record EquityAsset : Asset, ICashSettledAsset
{
    public override SettlementMode Settlement => SettlementMode.CashAndCarry;
    public decimal ShortMarginRate { get; init; } = 1.0m;

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
