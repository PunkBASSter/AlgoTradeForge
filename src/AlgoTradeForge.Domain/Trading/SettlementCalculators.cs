namespace AlgoTradeForge.Domain.Trading;

public static class SettlementCalculators
{
    public static ISettlementCalculator ForModel(SettlementModel model) => model switch
    {
        SettlementModel.CashAndCarry => CashAndCarrySettlement.Instance,
        SettlementModel.Margin => MarginSettlement.Instance,
        _ => throw new ArgumentOutOfRangeException(nameof(model), model, null)
    };
}
