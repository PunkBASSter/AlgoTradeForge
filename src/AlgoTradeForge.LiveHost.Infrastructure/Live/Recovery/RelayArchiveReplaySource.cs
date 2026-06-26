using System.Runtime.CompilerServices;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Live.Relay;
using AlgoTradeForge.LiveHost.Application.Live.Recovery;
using AlgoTradeForge.Storage;

namespace AlgoTradeForge.LiveHost.Infrastructure.Live.Recovery;

/// <summary>
/// Binance <see cref="IReplaySource"/>: streams archived trade ticks from relay <c>.atft</c>
/// segments under <c>{relayKeyPrefix}/{venue}/{instrument}/trades/</c>, oldest-first (segment
/// filenames sort chronologically by the <c>{createdAtMs:D13}-{firstSequence:D19}.atft</c>
/// convention), filtered to <see cref="ReplayRequest.FromTs"/>.
/// </summary>
/// <remarks>
/// EXTENSION POINT (deferred): a cold start whose <c>FromTs</c> predates the relay's earliest
/// retained segment needs the canonical tick archive
/// (<c>{DataRoot}/{venue}/{assetDir}/ticks/</c>, <c>assetDir = AssetDirectoryName.From(request.Asset)</c>),
/// read via the shared Infrastructure tick reader and stitched before the relay segments
/// (same <c>FromTs</c> filter; <see cref="ICatchupGate"/> dedupes the overlap). Wire this when
/// cold-start-beyond-retention is exercised; the reconnect window and recent cold starts are
/// covered by the relay path alone.
/// </remarks>
public sealed class RelayArchiveReplaySource(IFileStorage storage, string relayKeyPrefix) : IReplaySource
{
    public async IAsyncEnumerable<TradeTick> Replay(
        ReplayRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Binance crypto: the relay instrument key is the asset name (e.g. "BTCUSDT").
        var instrument = request.Asset.Name;
        var dir = $"{relayKeyPrefix}/{request.Venue}/{instrument}/trades";

        var keys = new List<string>();
        await foreach (var key in storage.ListKeys(dir, suffix: ".atft", recursive: false, ct))
            keys.Add(key);
        keys.Sort(StringComparer.Ordinal); // {createdAtMs:D13}-{firstSequence:D19}.atft → chronological

        foreach (var key in keys)
        {
            ct.ThrowIfCancellationRequested();
            using var stream = await storage.OpenRead(key, ct).ConfigureAwait(false);
            using var reader = new SegmentReader<TradeTick>(stream, leaveOpen: false);
            while (reader.TryRead(out var tick))
            {
                if (tick.TimestampMs < request.FromTs) continue;
                yield return tick;
            }
        }
    }
}
