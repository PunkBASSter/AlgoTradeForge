using System.Globalization;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Live.Relay;
using AlgoTradeForge.LiveHost.Application.Live.Recovery;
using AlgoTradeForge.LiveHost.Infrastructure.Live.Binance;
using AlgoTradeForge.Storage;

namespace AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

// IB gap policy (option C): fetch the gap window from reqHistoricalTicks and write it as a relay
// .atft segment so RelayArchiveReplaySource can re-read it contiguously. Returns true iff >=1 tick
// was archived. Zero budget short-circuits false (parity with BinanceBackfillRequester).
internal sealed class IbBackfillRequester(
    IIbHistoricalTicksClient client,
    IFileStorage storage,
    string relayKeyPrefix,
    IbDataPlaneOptions options,
    IIbContractResolver resolver,
    IIbInstrumentAssetResolver assets,
    TimeProvider time) : IBackfillRequester
{
    public async Task<bool> TryBackfill(
        ReplayRequest context, Discontinuity gap, RecoveryPolicy policy, CancellationToken ct = default)
    {
        if (policy.BackfillBudget <= TimeSpan.Zero) return false;

        var instrument = context.Asset.Name;
        var scale = options.InstrumentScales.TryGetValue(instrument, out var s) ? s : options.DefaultScale;
        var asset = await assets.Resolve(instrument, ct).ConfigureAwait(false);
        var resolved = await resolver.Resolve(asset.ToIbContract(), ct).ConfigureAwait(false);

        var ticks = await client.FetchTrades(resolved, gap.FromTs, gap.ToTs, ct).ConfigureAwait(false);
        if (ticks.Count == 0) return false;

        var createdAtMs = time.GetUtcNow().ToUnixTimeMilliseconds();
        const long firstSeq = 0L;
        var header = new SegmentHeader(
            PriceScaleExp: (sbyte)scale.PriceExp,
            QtyScaleExp: (sbyte)scale.QtyExp,
            EpochBaseMs: 0,
            CreatedAtMs: createdAtMs,
            FirstSequence: firstSeq,
            PayloadSize: (ushort)TradeTick.PayloadSize);

        using var ms = new MemoryStream();
        using (var writer = new SegmentWriter<TradeTick>(ms, in header, leaveOpen: true))
            foreach (var t in ticks)
                writer.Write(new TradeTick(
                    t.TimeSec * 1000,
                    scale.ScalePrice((decimal)t.Price),
                    scale.ScaleQty(t.Size),
                    Sequence: 0,
                    Aggressor: AggressorSide.Unknown));

        var name = string.Create(CultureInfo.InvariantCulture, $"{createdAtMs:D13}-{firstSeq:D19}.atft");
        var key = $"{relayKeyPrefix}/ib/{instrument}/trades/{name}";
        await storage.WriteAllBytes(key, ms.ToArray(), ct).ConfigureAwait(false);
        return true;
    }
}
