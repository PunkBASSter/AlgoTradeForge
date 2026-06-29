using AlgoTradeForge.Storage.Threading;
using IBApi;

namespace AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

// The single IB transport: owns one EClientSocket + EReader pump thread. Plan 1 uses it for
// reqContractDetails; Plan 3 grows IbSession around this exact primitive (tick streaming + shared order
// socket). The wrapper is supplied so the data/order planes can share one callback sink.
internal sealed class IbConnection(IbWrapper wrapper, IbConnectionOptions options) : IAsyncDisposable
{
    private EClientSocket? _client;
    private Thread? _readerThread;
    private EReaderMonitorSignal? _signal;
    private int _nextReqId; // single connection-scoped request-id source (tick subs, contract details, historical)
    private int _nextOrderId;   // order-id space, distinct from _nextReqId; seeded from nextValidId
    // Serializes Connect so concurrent callers (relay pump Stream, a bar-source Start, the reconnect worker)
    // cannot race two establish sequences onto one transport.
    private readonly SemaphoreSlim _connectGate = new(1, 1);

    public EClientSocket Client => _client ?? throw new InvalidOperationException("IB connection is not established.");

    // One id source for every request type on this socket: TWS correlates responses by (reqId, message-type)
    // but ALSO rejects a second active subscription reusing a live ticker id (error 322). Minting subscription,
    // contract-details, and historical-tick ids from separate counters collides; this is the shared allocator.
    public int NextReqId() => Interlocked.Increment(ref _nextReqId);

    // Re-arm the order-id counter to the server's seed on connect/reconnect. Upward-only
    // (a stale smaller reconnect seed must never rewind the counter); atomic against a
    // concurrent NextOrderId() via CAS so no allocated id is ever clobbered/reused.
    public void SeedNextOrderId(int seed)
    {
        int current;
        do { current = Volatile.Read(ref _nextOrderId); }
        while (seed > current && Interlocked.CompareExchange(ref _nextOrderId, seed, current) != current);
    }

    // One id per order (brackets are individual strategy-side orders — no consecutive reservation).
    public int NextOrderId() => Interlocked.Increment(ref _nextOrderId) - 1; // returns seed, then seed+1, …

    // 90 attempts (~3 min): gateway cold start (IBC login + API socket bind) routinely exceeds 60s, and the
    // first socket is often reset once by the 10141 paper-trading disclaimer before the API binds.
    // Idempotent: a call while the socket is already alive is a no-op (so a defensive Connect from a bar source,
    // or a 1101 "re-subscribe" recovery on a still-alive socket, costs nothing). A reconnect after a real drop
    // reaches the establish path with IsConnected()==false.
    public async Task Connect(int maxAttempts = 90, int retryDelayMs = 2000, CancellationToken ct = default)
    {
        using var _ = await _connectGate.LockAsync(ct).ConfigureAwait(false);
        if (_client?.IsConnected() == true) return;

        // Tear down any prior (now-dropped) socket + parked pump BEFORE re-establishing, so a successful
        // reconnect never leaks the old EReader thread or an orphaned socket dispatching into the shared wrapper.
        Disconnect();

        Exception? lastError = null;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            // Fresh signal per attempt: a torn-down attempt's parked pump thread must never share a signal
            // with the next attempt's EReader, or signals dispatch to the wrong (dead) socket.
            wrapper.ResetForReconnect();
            var signal = new EReaderMonitorSignal();
            var client = new EClientSocket(wrapper, signal);
            var connected = false;
            try
            {
                client.eConnect(options.Host, options.Port, options.ClientId);
                if (client.IsConnected())
                {
                    _client = client;
                    StartReaderPump(client, signal);
                    await wrapper.NextValidId.WaitAsync(TimeSpan.FromSeconds(15), ct);
                    SeedNextOrderId(await wrapper.NextValidId);
                    connected = true;
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                throw; // teardown runs in finally; rethrow so cancellation propagates instead of retrying
            }
            catch (Exception ex)
            {
                // transient gateway-cold-start / disclaimer-reset failure; tear down (finally) and retry below
                lastError = ex;
            }
            finally
            {
                if (!connected)
                    Teardown(client);
            }
            if (attempt < maxAttempts)
                await Task.Delay(retryDelayMs, ct);
        }
        throw new TimeoutException($"Could not connect to IB Gateway at {options.Host}:{options.Port}.", lastError);
    }

    private void StartReaderPump(EClientSocket client, EReaderMonitorSignal signal)
    {
        _signal = signal;
        var reader = new EReader(client, signal);
        reader.Start();
        _readerThread = new Thread(() =>
        {
            while (client.IsConnected())
            {
                signal.waitForSignal();
                reader.processMsgs();
            }
        }) { IsBackground = true, Name = "ib-ereader" };
        _readerThread.Start();
    }

    // Disconnects a failed attempt's socket so its pump thread's while(IsConnected) loop exits, and clears the
    // active references so Client stays "not established" until a real success.
    private void Teardown(EClientSocket client)
    {
        if (client.IsConnected())
            client.eDisconnect();
        WakeAndJoinPump();
        if (ReferenceEquals(_client, client))
            _client = null;
    }

    public void Disconnect()
    {
        if (_client?.IsConnected() == true)
            _client.eDisconnect();          // breaks the while(IsConnected) loop condition
        WakeAndJoinPump();
    }

    // Unparks a pump blocked in waitForSignal() and waits for it to exit. Safe as a no-op when
    // no pump was started (both fields are null) or after a prior call already cleared them.
    private void WakeAndJoinPump()
    {
        var thread = _readerThread;
        _signal?.issueSignal();              // unpark a pump blocked in waitForSignal()
        thread?.Join(TimeSpan.FromSeconds(5)); // bounded: never hang on a stuck pump
        _readerThread = null;
        _signal = null;
    }

    public ValueTask DisposeAsync()
    {
        Disconnect();
        _connectGate.Dispose();
        return ValueTask.CompletedTask;
    }
}
