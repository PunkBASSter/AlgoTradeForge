namespace AlgoTradeForge.Domain.Strategy;

public interface IOrderContextReceiver
{
    void SetOrderContext(IOrderContext context);
}
