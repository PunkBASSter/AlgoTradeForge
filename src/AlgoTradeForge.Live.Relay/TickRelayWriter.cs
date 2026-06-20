using System.Threading.Channels;
using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.Live.Relay;

public sealed class TickRelayWriter : IAsyncDisposable
{
    private const int HeartbeatCommandId = -1;

    private readonly ITickSegmentSink _sink;
    private readonly TickRelayOptions _options;
    private readonly TimeProvider _time;
    private readonly Channel<Envelope> _channel;
    // Copy-on-write: appended under _registrationLock, read lock-free by the drain thread.
    private volatile InstrumentState[] _instruments = [];
    private readonly Lock _registrationLock = new();
    private readonly Task _drain;
    private readonly Task _heartbeat;
    private readonly CancellationTokenSource _cts = new();

    private long _dropped;
    private bool _disposed;
    private TaskCompletionSource _drainIdle = NewIdleSource();

    public TickRelayWriter(ITickSegmentSink sink, TickRelayOptions options, TimeProvider time)
    {
        _sink = sink;
        _options = options;
        _time = time;
        _channel = Channel.CreateBounded<Envelope>(new BoundedChannelOptions(options.ChannelCapacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        _drain = Task.Run(DrainLoop);
        _heartbeat = Task.Run(HeartbeatLoop);
    }

    public long DroppedCount => Interlocked.Read(ref _dropped);

    public int RegisterInstrument(string instrument, sbyte priceScaleExp, sbyte qtyScaleExp)
    {
        using (_registrationLock.EnterScope())
        {
            var next = new InstrumentState[_instruments.Length + 1];
            Array.Copy(_instruments, next, _instruments.Length);
            next[^1] = new InstrumentState
            {
                Instrument = instrument,
                PriceScaleExp = priceScaleExp,
                QtyScaleExp = qtyScaleExp,
            };
            _instruments = next;
            return next.Length - 1;
        }
    }

    public bool TryEnqueue(int instrumentId, in TradeTick tick)
    {
        if (_channel.Writer.TryWrite(new Envelope(instrumentId, tick))) return true;
        Interlocked.Increment(ref _dropped);
        return false;
    }

    public ValueTask Enqueue(int instrumentId, TradeTick tick, CancellationToken ct = default) =>
        _channel.Writer.WriteAsync(new Envelope(instrumentId, tick), ct);

    // Test support: completes once the drain has caught up to everything enqueued so far.
    public Task WaitForDrain()
    {
        var probe = Volatile.Read(ref _drainIdle);
        _channel.Writer.TryWrite(new Envelope(HeartbeatCommandId, default));
        return probe.Task;
    }

    private async Task DrainLoop()
    {
        var reader = _channel.Reader;
        try
        {
            while (await reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
            {
                while (reader.TryRead(out var env))
                    await Handle(env).ConfigureAwait(false);

                var idle = Interlocked.Exchange(ref _drainIdle, NewIdleSource());
                idle.TrySetResult();
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            var instruments = _instruments;
            foreach (var st in instruments)
                await CloseSegment(st, SessionBoundaryReason.SessionEnd).ConfigureAwait(false);
            Volatile.Read(ref _drainIdle).TrySetResult();
        }
    }

    private async Task Handle(Envelope env)
    {
        var instruments = _instruments;
        if (env.InstrumentId == HeartbeatCommandId)
        {
            long now = _time.GetUtcNow().ToUnixTimeMilliseconds();
            foreach (var st in instruments)
                st.Writer?.WriteHeartbeat(now);
            return;
        }

        var state = instruments[env.InstrumentId];
        await EnsureSegment(state, env.Trade.Sequence).ConfigureAwait(false);
        if (state.BytesInSegment + RelayFormat.FrameSize > _options.MaxSegmentBytes)
        {
            await CloseSegment(state, null).ConfigureAwait(false);
            await EnsureSegment(state, env.Trade.Sequence).ConfigureAwait(false);
        }

        state.Writer!.WriteTick(env.Trade);
        state.BytesInSegment += RelayFormat.FrameSize;
    }

    private async ValueTask EnsureSegment(InstrumentState st, long firstSequence)
    {
        if (st.Writer is not null) return;

        long now = _time.GetUtcNow().ToUnixTimeMilliseconds();
        // Segment open is part of the drain's normal work — uncancellable, like CloseSegment's finalize.
        st.Stream = await _sink.BeginSegment(st.Instrument, firstSequence, now, CancellationToken.None).ConfigureAwait(false);
        var header = new TickSegmentHeader(st.PriceScaleExp, st.QtyScaleExp, 0, now, firstSequence);
        try
        {
            st.Writer = new TickSegmentWriter(st.Stream, header, leaveOpen: true);
        }
        catch
        {
            st.Stream.Dispose();
            st.Stream = null;
            throw;
        }
        st.BytesInSegment = RelayFormat.HeaderSize;
        st.Writer.WriteSessionBoundary(now, SessionBoundaryReason.SessionStart);
        st.BytesInSegment += RelayFormat.FrameSize;
    }

    private async Task CloseSegment(InstrumentState st, SessionBoundaryReason? finalMarker)
    {
        if (st.Writer is null || st.Stream is null) return;

        if (finalMarker is { } reason)
            st.Writer.WriteSessionBoundary(_time.GetUtcNow().ToUnixTimeMilliseconds(), reason);

        st.Writer.Flush(toDisk: true);
        await _sink.CompleteSegment(st.Instrument, st.Stream, CancellationToken.None).ConfigureAwait(false);
        st.Writer = null;
        st.Stream = null;
        st.BytesInSegment = 0;
    }

    private async Task HeartbeatLoop()
    {
        try
        {
            // PeriodicTimer(TimeSpan, TimeProvider) overload lets FakeTimeProvider drive ticks deterministically.
            using var timer = new PeriodicTimer(_options.HeartbeatInterval, _time);
            while (await timer.WaitForNextTickAsync(_cts.Token).ConfigureAwait(false))
                _channel.Writer.TryWrite(new Envelope(HeartbeatCommandId, default));
        }
        catch (OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        // Complete the channel before cancelling _cts so the drain finishes every queued
        // envelope and writes each instrument's SessionEnd boundary before heartbeat teardown.
        _channel.Writer.TryComplete();
        await _drain.ConfigureAwait(false);
        _cts.Cancel();
        try { await _heartbeat.ConfigureAwait(false); } catch (OperationCanceledException) { }
        _cts.Dispose();
    }

    private static TaskCompletionSource NewIdleSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly record struct Envelope(int InstrumentId, TradeTick Trade);

    private sealed class InstrumentState
    {
        public required string Instrument { get; init; }
        public sbyte PriceScaleExp { get; init; }
        public sbyte QtyScaleExp { get; init; }
        public TickSegmentWriter? Writer { get; set; }
        public Stream? Stream { get; set; }
        public long BytesInSegment { get; set; }
    }
}
