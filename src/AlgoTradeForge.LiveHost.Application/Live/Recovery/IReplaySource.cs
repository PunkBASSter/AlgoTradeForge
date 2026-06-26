using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.LiveHost.Application.Live.Recovery;

/// <summary>
/// Yields archived source ticks at or after <see cref="ReplayRequest.FromTs"/>, in venue aggId
/// order, stitching recent relay segments with the deeper canonical archive. Venue-specific impl.
/// </summary>
public interface IReplaySource
{
    IAsyncEnumerable<TradeTick> Replay(ReplayRequest request, CancellationToken ct = default);
}
