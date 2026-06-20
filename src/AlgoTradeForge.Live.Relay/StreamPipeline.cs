using System.Threading.Channels;
using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.Live.Relay;

public sealed class StreamPipeline<T> : IAsyncDisposable where T : struct, IFramePayload<T>
{
    private const int DrainSentinelId = -1;

    private readonly ISegmentSink _sink;
    private readonly StreamPipelineOptions _options;
    private readonly TimeProvider _time;
    private readonly Channel<Envelope> _channel;
    // Copy-on-write: appended under _registrationLock, read lock-free by the drain thread.
    private volatile InstrumentState[] _instruments = [];
    private readonly Lock _registrationLock = new();
    private readonly Task _drain;
    private readonly CancellationTokenSource _cts = new();

    private long _dropped;
    private bool _disposed;
    private TaskCompletionSource _drainIdle = NewIdleSource();

    public StreamPipeline(ISegmentSink sink, StreamPipelineOptions options, TimeProvider time)
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

    public bool TryEnqueue(int instrumentId, in T payload)
    {
        if (_channel.Writer.TryWrite(new Envelope(instrumentId, payload))) return true;
        Interlocked.Increment(ref _dropped);
        return false;
    }

    public ValueTask Enqueue(int instrumentId, T payload, CancellationToken ct = default) =>
        _channel.Writer.WriteAsync(new Envelope(instrumentId, payload), ct);

    // Test support: completes once the drain has caught up to everything enqueued so far.
    public Task WaitForDrain()
    {
        var probe = Volatile.Read(ref _drainIdle);
        _channel.Writer.TryWrite(new Envelope(DrainSentinelId, default));
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
                await CloseSegment(st).ConfigureAwait(false);
            Volatile.Read(ref _drainIdle).TrySetResult();
        }
    }

    private async Task Handle(Envelope env)
    {
        if (env.InstrumentId == DrainSentinelId) return;

        var instruments = _instruments;
        var state = instruments[env.InstrumentId];
        await EnsureSegment(state, env.Payload.Sequence).ConfigureAwait(false);
        if (state.BytesInSegment + T.PayloadSize > _options.MaxSegmentBytes)
        {
            await CloseSegment(state).ConfigureAwait(false);
            await EnsureSegment(state, env.Payload.Sequence).ConfigureAwait(false);
        }

        state.Writer!.Write(env.Payload);
        state.BytesInSegment += T.PayloadSize;
    }

    private async ValueTask EnsureSegment(InstrumentState st, long firstSequence)
    {
        if (st.Writer is not null) return;

        long now = _time.GetUtcNow().ToUnixTimeMilliseconds();
        st.Stream = await _sink.BeginSegment(T.StreamName, st.Instrument, firstSequence, now, CancellationToken.None).ConfigureAwait(false);
        var header = new SegmentHeader(st.PriceScaleExp, st.QtyScaleExp, 0, now, firstSequence, (ushort)T.PayloadSize);
        try
        {
            st.Writer = new SegmentWriter<T>(st.Stream, header, leaveOpen: true);
        }
        catch
        {
            st.Stream.Dispose();
            st.Stream = null;
            throw;
        }
        st.BytesInSegment = SegmentHeader.Size;
    }

    private async Task CloseSegment(InstrumentState st)
    {
        if (st.Writer is null || st.Stream is null) return;

        st.Writer.Flush(toDisk: true);
        await _sink.CompleteSegment(T.StreamName, st.Instrument, st.Stream, CancellationToken.None).ConfigureAwait(false);
        st.Writer = null;
        st.Stream = null;
        st.BytesInSegment = 0;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        // Complete the channel before cancelling _cts so the drain finishes every queued envelope before teardown.
        _channel.Writer.TryComplete();
        await _drain.ConfigureAwait(false);
        _cts.Cancel();
        _cts.Dispose();
    }

    private static TaskCompletionSource NewIdleSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly record struct Envelope(int InstrumentId, T Payload);

    private sealed class InstrumentState
    {
        public required string Instrument { get; init; }
        public sbyte PriceScaleExp { get; init; }
        public sbyte QtyScaleExp { get; init; }
        public SegmentWriter<T>? Writer { get; set; }
        public Stream? Stream { get; set; }
        public long BytesInSegment { get; set; }
    }
}
