namespace AlgoTradeForge.Domain.Engine;

/// <summary>
/// No-op probe for normal (non-debug) backtest runs.
/// <see cref="IsActive"/> returns false, so the engine never calls any methods.
/// </summary>
public sealed class NullDebugProbe : IDebugProbe
{
    public static readonly NullDebugProbe Instance = new();
    private NullDebugProbe() { }

    public bool IsActive => false;
    public void OnRunStart() { }
    public void OnBarProcessed(DebugSnapshot snapshot) { }
    public void OnRunEnd() { }
}
