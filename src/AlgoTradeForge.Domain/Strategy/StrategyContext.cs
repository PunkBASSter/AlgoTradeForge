using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.Domain.Strategy;

public sealed class StrategyContext(
    Asset currentAsset,
    OhlcvBar currentBar,
    int barIndex,
    Portfolio portfolio,
    IReadOnlyList<Fill> fills,
    IReadOnlyList<OhlcvBar> barHistory)
{
    public Asset CurrentAsset => currentAsset;
    public OhlcvBar CurrentBar => currentBar;
    public int BarIndex => barIndex;
    public Portfolio Portfolio => portfolio;
    public IReadOnlyList<Fill> Fills => fills;
    public IReadOnlyList<OhlcvBar> BarHistory => barHistory;

    public decimal CurrentPrice => currentBar.Close;
    public decimal Cash => portfolio.Cash;
    public decimal PositionQuantity => portfolio.GetPosition(currentAsset.Name)?.Quantity ?? 0m;
    public decimal Equity => portfolio.Equity(currentBar.Close);
}
