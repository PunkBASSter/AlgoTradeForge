namespace AlgoTradeForge.Domain.Trading;

public static class SettlementCalculatorExtensions
{
    public static ISettlementCalculator GetSettlementCalculator(this Asset asset) => asset.Settlement switch
    {
        SettlementMode.CashAndCarry => CashAndCarrySettlement.Instance,
        SettlementMode.Margin => MarginSettlement.Instance,
        _ => throw new ArgumentOutOfRangeException(nameof(asset.Settlement))
    };
}
