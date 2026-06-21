using AlgoTradeForge.HistoryLoader.Application.Canonicalization;
using AlgoTradeForge.HistoryLoader.Infrastructure.Canonicalization;
using AlgoTradeForge.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.HistoryLoader.WebApi.Collection;

/// <summary>
/// Tails the uploaded live-md/{venue}/ relay prefix and canonicalizes each stream. Idle until a
/// LiveHost producer (Plan 3) uploads segments. Config-gated by Canonicalizer:Enabled.
/// </summary>
internal sealed class TickCanonicalizerService(
    IEnumerable<IStreamCanonicalizer> canonicalizers,
    IFileStorage storage,
    IOptions<CanonicalizerOptions> options,
    ILogger<TickCanonicalizerService> logger) : BackgroundService
{
    private readonly CanonicalizerOptions _options = options.Value;

    private static bool IsTrueShutdown(Exception ex, CancellationToken token) =>
        ex is OperationCanceledException oce && token.IsCancellationRequested && oce.CancellationToken == token;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            logger.LogInformation("TickCanonicalizerService disabled (Canonicalizer:Enabled=false)");
            return;
        }

        if (string.IsNullOrWhiteSpace(_options.Venue))
        {
            logger.LogWarning("TickCanonicalizerService enabled but Canonicalizer:Venue is empty — no streams to discover; idling.");
            return;
        }

        var byStream = canonicalizers.ToDictionary(c => c.StreamName, StringComparer.Ordinal);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, _options.PollIntervalSeconds)));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken)) break;
                await CanonicalizeCycle(byStream, stoppingToken);
            }
            catch (Exception ex) when (!IsTrueShutdown(ex, stoppingToken))
            {
                logger.LogError(ex, "Canonicalize cycle failed; will retry next tick");
            }
        }
    }

    private async Task CanonicalizeCycle(
        IReadOnlyDictionary<string, IStreamCanonicalizer> byStream, CancellationToken ct)
    {
        var venuePrefix = $"{_options.LiveMdPrefix}/{_options.Venue}/";
        var seen = new HashSet<(string instrumentOrVenue, string stream)>();

        await foreach (var key in storage.ListKeys(venuePrefix, ".atft", recursive: true, ct))
        {
            if (!SegmentKeyParser.TryParse(key, _options.LiveMdPrefix, out var loc)) continue;
            seen.Add((loc.InstrumentOrVenue, loc.StreamName));
        }

        foreach (var (instrumentOrVenue, stream) in seen)
        {
            ct.ThrowIfCancellationRequested();
            if (!byStream.TryGetValue(stream, out var canon)) continue; // unknown stream type — skip
            var n = await canon.Run(_options.Venue, instrumentOrVenue, ct);
            if (n > 0)
                logger.LogInformation("Canonicalized {Count} {Stream} frames for {Instrument}",
                    n, stream, instrumentOrVenue);
        }
    }
}
