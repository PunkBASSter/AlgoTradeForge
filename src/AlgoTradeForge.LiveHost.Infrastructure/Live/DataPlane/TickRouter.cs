using System.Collections.Generic;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.Subscriptions;
using AlgoTradeForge.LiveHost.Application.Live.DataPlane;
using Microsoft.Extensions.Logging;

namespace AlgoTradeForge.LiveHost.Infrastructure.Live.DataPlane;

// Owns SHARED bar sources (one per (instrument, BarSpecKey), reused across sessions). On each
// published tick it feeds the tick-fed sources for that instrument and fans the raw tick to dispatch.
//
// Concurrency: lifecycle mutations (EnsureSources/RemoveSources) run under a single lock and
// rebuild-and-swap an immutable per-instrument tick-fed snapshot. Publish (the hot path) reads
// that snapshot through a volatile reference with NO lock and NO per-tick allocation — mirroring
// the Plan-1 relay's copy-on-write volatile instrument publication.
public sealed class TickRouter(IBarSourceResolver resolver, IStrategyDispatch dispatch, ILogger<TickRouter> logger)
    : ITickRouter
{
    private sealed class SourceEntry(IBarSource source)
    {
        public IBarSource Source { get; } = source;
        public int RefCount { get; set; } = 1;
    }

    private readonly IBarSourceResolver _resolver = resolver;
    private readonly IStrategyDispatch _dispatch = dispatch;
    private readonly ILogger<TickRouter> _logger = logger;

    private readonly Lock _lifecycleLock = new();
    private readonly Dictionary<(string Instrument, BarSpecKey Spec), SourceEntry> _sources = new();
    private readonly Dictionary<Guid, List<(string Instrument, BarSpecKey Spec)>> _sessionKeys = new();

    // Copy-on-write snapshot read lock-free by Publish; rebuilt+swapped under _lifecycleLock.
    private volatile Dictionary<string, ITickDrivenBarSource[]> _tickFed = new();

    public void Publish(string instrument, in TradeTick tick)
    {
        var snapshot = _tickFed;
        if (snapshot.TryGetValue(instrument, out var sources))
        {
            for (var i = 0; i < sources.Length; i++)
                sources[i].Feed(in tick); // each source's onBar -> dispatch.DispatchBar(instrument, spec, bar, false)
        }

        _dispatch.DispatchTick(instrument, in tick); // raw ticks always fan out; dispatch gates on tick-subscribers
    }

    public IReadOnlyList<Int64Bar> RecentBars(string instrument, BarSpecKey spec)
    {
        IBarSource? source;
        using (_lifecycleLock.EnterScope())
            source = _sources.TryGetValue((instrument, spec), out var entry) ? entry.Source : null;

        // Recent is itself a snapshot copy (read outside the lock to avoid holding it during the copy).
        return source?.Recent ?? [];
    }

    public async ValueTask EnsureSources(LiveSessionRegistration r, Func<string, ScaleContext> scaleFor)
    {
        // Newly-created sources to start AFTER the lock — venue sources (kline WS) subscribe in Start().
        List<IBarSource>? toStart = null;
        using (_lifecycleLock.EnterScope())
        {
            // Idempotency guard: a session registers exactly once. Skip a duplicate so a repeated
            // call can't double-increment RefCounts and leak sources on RemoveSources.
            if (_sessionKeys.ContainsKey(r.SessionId)) return;

            var keys = new List<(string, BarSpecKey)>();
            foreach (var raw in r.RawSubscriptions)
            {
                // INSTRUMENT KEY CONTRACT (matches StrategyDispatch/SessionInterest): instrument == AssetName.
                var instrument = raw.AssetName;
                var spec = SpecFor(raw);
                if (spec is null) continue; // TickSubscription / unknown -> no bar source

                var key = (instrument, spec.Value);
                if (_sources.TryGetValue(key, out var existing))
                {
                    existing.RefCount++; // reused shared source: already started, do NOT start again
                }
                else
                {
                    var specValue = spec.Value;
                    // onBar captures instrument+spec locals (closure created once at Ensure time, not per-tick).
                    Action<Int64Bar, bool> onBar = (bar, isStart) => _dispatch.DispatchBar(instrument, specValue, in bar, isStart);
                    var source = _resolver.Resolve(instrument, raw, scaleFor(instrument), onBar);
                    if (source is null) continue;
                    _sources[key] = new SourceEntry(source);
                    (toStart ??= []).Add(source); // start exactly once, on first creation
                }

                keys.Add(key);
            }

            if (keys.Count == 0) return;

            if (_sessionKeys.TryGetValue(r.SessionId, out var existingKeys))
                existingKeys.AddRange(keys);
            else
                _sessionKeys[r.SessionId] = keys;

            RebuildTickFed();
        }

        // Start outside the lock — venue-source subscription may do WS I/O.
        if (toStart is null) return;
        foreach (var source in toStart)
            await source.Start();
    }

    public async ValueTask RemoveSources(Guid sessionId)
    {
        List<IBarSource>? toDispose = null;
        using (_lifecycleLock.EnterScope())
        {
            if (!_sessionKeys.Remove(sessionId, out var keys)) return;

            foreach (var key in keys)
            {
                if (!_sources.TryGetValue(key, out var entry)) continue;
                if (--entry.RefCount > 0) continue;
                _sources.Remove(key);
                (toDispose ??= []).Add(entry.Source);
            }

            RebuildTickFed();
        }

        // Dispose outside the lock — kline-source (T14) teardown may do I/O.
        if (toDispose is null) return;
        foreach (var source in toDispose)
            await Dispose(source);
    }

    // Rebuilds the immutable per-instrument tick-fed lookup and atomically swaps the volatile field.
    private void RebuildTickFed()
    {
        var byInstrument = new Dictionary<string, List<ITickDrivenBarSource>>();
        foreach (var (key, entry) in _sources)
        {
            if (entry.Source is not ITickDrivenBarSource tickFed) continue;
            if (!byInstrument.TryGetValue(key.Instrument, out var list))
                byInstrument[key.Instrument] = list = [];
            list.Add(tickFed);
        }

        var snapshot = new Dictionary<string, ITickDrivenBarSource[]>(byInstrument.Count);
        foreach (var (instrument, list) in byInstrument)
            snapshot[instrument] = list.ToArray();

        _tickFed = snapshot;
    }

    private static BarSpecKey? SpecFor(DataFeedSubscription raw) => raw switch
    {
        TimeBarSubscription tb => BarSpecKey.TimeBar(tb.TimeFrame),
        AltBarSubscription ab => BarSpecKey.AltBar(ab.FeedId),
        _ => null,
    };

    private static async ValueTask Dispose(IBarSource source)
    {
        switch (source)
        {
            case IAsyncDisposable a: // kline sources (T14) are IAsyncDisposable; teardown may do I/O
                await a.DisposeAsync();
                break;
            case IDisposable d:
                d.Dispose();
                break;
        }
    }
}
