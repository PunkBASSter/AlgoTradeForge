using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.Domain.Strategy;

public sealed class StrategyContext(
    OhlcvBar currentBar,
    int barIndex,
    Portfolio portfolio,
    IReadOnlyList<Fill> fills,
    IReadOnlyList<OhlcvBar> barHistory)
{
    public OhlcvBar CurrentBar => currentBar;
    public int BarIndex => barIndex;
    public Portfolio Portfolio => portfolio;
    public IReadOnlyList<Fill> Fills => fills;
    public IReadOnlyList<OhlcvBar> BarHistory => barHistory;

    public decimal CurrentPrice => currentBar.Close;
    public decimal Cash => portfolio.Cash;
    public decimal PositionQuantity => portfolio.Position.Quantity;
    public decimal Equity => portfolio.Equity(currentBar.Close);
}
