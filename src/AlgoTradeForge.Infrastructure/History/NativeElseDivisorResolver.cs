using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy;

namespace AlgoTradeForge.Infrastructure.History;

/// <summary>
/// Vendor-native archives (equity, futures) with no single guaranteed source interval:
/// candle files are stored at whichever intervals the vendor supplied (e.g. 5m + 1d).
/// Loads the requested timeframe natively when present, else resamples from the largest
/// available interval that cleanly divides it. Requires the manifest to enumerate its
/// intervals — a missing/unreadable/empty manifest is a hard error, never a silent
/// fall-through to a source interval this archive does not have.
/// </summary>
public sealed class NativeElseDivisorResolver(IFeedManifestReader manifestReader, string dataRoot)
    : IHistoryFeedResolver
{
    public async Task<FeedResolution> Resolve(Asset asset, TimeFrame requested, CancellationToken ct = default)
    {
        var manifest = await manifestReader.Read(dataRoot, asset.Exchange, AssetDirectoryName.From(asset), ct);
        var intervals = manifest?.Candles?.Intervals;
        if (intervals is null || intervals.Length == 0)
            throw new ArgumentException(
                $"No candle intervals available for {asset.Exchange}/{asset.Name}; cannot load timeframe " +
                $"'{requested.Code}'. feeds.json is missing, unreadable, or declares no candles.intervals.",
                nameof(requested));

        var parsed = intervals
            .Select(code => (code, dur: TryDuration(code)))
            .Where(x => x.dur is not null)
            .ToArray();

        // Native match is by duration, not string, so a "60m" partition still satisfies a "1h" request.
        foreach (var (code, dur) in parsed)
            if (dur == requested.Duration)
                return new FeedResolution(code, Resample: false);

        var divisor = parsed
            .Where(x => x.dur < requested.Duration && requested.Duration.Ticks % x.dur!.Value.Ticks == 0)
            .OrderByDescending(x => x.dur!.Value)
            .Select(x => x.code)
            .FirstOrDefault();
        if (divisor is not null)
            return new FeedResolution(divisor, Resample: true);

        throw new ArgumentException(
            $"No feed on disk can produce timeframe '{requested.Code}': available intervals = " +
            $"[{string.Join(", ", intervals)}]. It is neither present natively nor a clean multiple " +
            "of a finer available interval.",
            nameof(requested));
    }

    private static TimeSpan? TryDuration(string code) =>
        TimeFrame.TryParse(code, out var tf) ? tf.Duration : null;
}
