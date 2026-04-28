using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.Domain.Strategy;

/// <summary>
/// Sentinel that throws on any access. Replaces <c>null!</c> as the default
/// for <see cref="StrategyBase{TParams}.Orders"/> so a missing
/// <see cref="IOrderContextReceiver.SetOrderContext"/> call produces a clear
/// error instead of a <see cref="NullReferenceException"/>.
/// </summary>
public sealed class UninitializedOrderContext : IOrderContext
{
    public static readonly UninitializedOrderContext Instance = new();

    private static InvalidOperationException NotInitialized() =>
        new("IOrderContext has not been initialized. " +
            "Ensure the engine calls SetOrderContext before any strategy lifecycle methods.");

    public long Cash => throw NotInitialized();
    public long UsedMargin => throw NotInitialized();
    public long AvailableMargin => throw NotInitialized();
    public long Submit(Order order) => throw NotInitialized();
    public Order? Cancel(long orderId) => throw NotInitialized();
    public IReadOnlyList<Order> GetPendingOrders() => throw NotInitialized();
    public IReadOnlyList<Fill> GetFills() => throw NotInitialized();
    public IReadOnlyDictionary<string, Position> GetPositions() => throw NotInitialized();
}
