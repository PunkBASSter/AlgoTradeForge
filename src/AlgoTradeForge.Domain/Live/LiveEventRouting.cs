namespace AlgoTradeForge.Domain.Live;

[Flags]
public enum LiveEventRouting
{
    None = 0,
    OnBarStart = 1,
    OnBarComplete = 2,
    OnTrade = 4,
    All = OnBarStart | OnBarComplete | OnTrade,
}
