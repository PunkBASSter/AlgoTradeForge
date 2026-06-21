using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.Live.Relay;

/// <summary>
/// Coordinates per-type StreamPipelines (trades, quotes, _session).
/// Call Start() before writing: it emits SessionStart and begins the heartbeat loop.
/// DisposeAsync ordering: stop heartbeat → write SessionEnd → drain all pipelines (session last).
/// </summary>
public sealed class RelayWriter : IAsyncDisposable
{
    private readonly string _venue;
    private readonly StreamPipeline<TradeTick> _trades;
    private readonly StreamPipeline<QuoteTick> _quotes;
    private readonly StreamPipeline<SessionEvent> _session;
    private readonly TimeProvider _time;
    private readonly TimeSpan _heartbeatInterval;
    private readonly int _sessionInstrumentId;
    private readonly CancellationTokenSource _heartbeatCts = new();

    private Task _heartbeatTask = Task.CompletedTask;
    private readonly TaskCompletionSource _heartbeatReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _started;
    private bool _disposed;

    public RelayWriter(string venue, ISegmentSink sink, StreamPipelineOptions options, TimeProvider time, TimeSpan heartbeatInterval)
    {
        _venue = venue;
        _time = time;
        _heartbeatInterval = heartbeatInterval;
        _trades = new StreamPipeline<TradeTick>(sink, options, time);
        _quotes = new StreamPipeline<QuoteTick>(sink, options, time);
        _session = new StreamPipeline<SessionEvent>(sink, options, time);
        // The session pipeline uses the venue name as its single "instrument".
        _sessionInstrumentId = _session.RegisterInstrument(venue, priceScaleExp: 0, qtyScaleExp: 0);
    }

    /// <summary>
    /// Registers the instrument on both the trades and quotes pipelines and returns the shared id.
    /// Ids stay in lockstep because RegisterInstrument is the only mutator and always hits both.
    /// </summary>
    public int RegisterInstrument(string instrument, sbyte priceScaleExp, sbyte qtyScaleExp)
    {
        int tradesId = _trades.RegisterInstrument(instrument, priceScaleExp, qtyScaleExp);
        _quotes.RegisterInstrument(instrument, priceScaleExp, qtyScaleExp);
        return tradesId;
    }

    /// <summary>
    /// Emits SessionStart and starts the heartbeat timer. Must be called before any Write*.
    /// </summary>
    public async ValueTask Start(CancellationToken ct = default)
    {
        if (_started) throw new InvalidOperationException("RelayWriter already started.");
        long now = _time.GetUtcNow().ToUnixTimeMilliseconds();
        await _session.Enqueue(_sessionInstrumentId, new SessionEvent(now, SessionEventKind.SessionStart), ct).ConfigureAwait(false);
        _heartbeatTask = Task.Run(() => HeartbeatLoop(_heartbeatCts.Token));
        await _heartbeatReady.Task.ConfigureAwait(false);
        _started = true;
    }

    public async Task WaitForDrain()
    {
        await _trades.WaitForDrain().ConfigureAwait(false);
        await _quotes.WaitForDrain().ConfigureAwait(false);
        await _session.WaitForDrain().ConfigureAwait(false);
    }

    public ValueTask WriteTrade(int instrumentId, TradeTick t, CancellationToken ct = default) =>
        _trades.Enqueue(instrumentId, t, ct);

    public ValueTask WriteQuote(int instrumentId, QuoteTick q, CancellationToken ct = default) =>
        _quotes.Enqueue(instrumentId, q, ct);

    public ValueTask WriteSessionEvent(SessionEvent e, CancellationToken ct = default) =>
        _session.Enqueue(_sessionInstrumentId, e, ct);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        // Stop heartbeat first so no new heartbeat events race with SessionEnd.
        _heartbeatCts.Cancel();
        // Await the heartbeat loop fully before draining _session: its in-flight enqueue is unblocked by the still-running session drain, and completing the channel only after it exits rules out a write-after-complete.
        try { await _heartbeatTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
        _heartbeatCts.Dispose();

        // Write SessionEnd before draining so it lands in the session stream.
        long now = _time.GetUtcNow().ToUnixTimeMilliseconds();
        await _session.Enqueue(_sessionInstrumentId, new SessionEvent(now, SessionEventKind.SessionEnd)).ConfigureAwait(false);

        // Drain data pipelines first, then session last so SessionEnd is included before its drain.
        await _trades.DisposeAsync().ConfigureAwait(false);
        await _quotes.DisposeAsync().ConfigureAwait(false);
        await _session.DisposeAsync().ConfigureAwait(false);
    }

    private async Task HeartbeatLoop(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(_heartbeatInterval, _time);
            _heartbeatReady.TrySetResult();
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                long now = _time.GetUtcNow().ToUnixTimeMilliseconds();
                await WriteSessionEvent(new SessionEvent(now, SessionEventKind.Heartbeat)).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _heartbeatReady.TrySetResult();
        }
    }
}
