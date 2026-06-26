using IBApi;

namespace AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

// The single IB transport: owns one EClientSocket + EReader pump thread. Plan 1 uses it for
// reqContractDetails; Plan 3 grows IbSession around this exact primitive (tick streaming + shared order
// socket). The wrapper is supplied so the data/order planes can share one callback sink.
internal sealed class IbConnection(IbWrapper wrapper, IbConnectionOptions options) : IAsyncDisposable
{
    private EClientSocket? _client;
    private Thread? _readerThread;

    public EClientSocket Client => _client ?? throw new InvalidOperationException("IB connection is not established.");

    // 90 attempts (~3 min): gateway cold start (IBC login + API socket bind) routinely exceeds 60s, and the
    // first socket is often reset once by the 10141 paper-trading disclaimer before the API binds.
    public async Task Connect(int maxAttempts = 90, int retryDelayMs = 2000, CancellationToken ct = default)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            // Fresh signal per attempt: a torn-down attempt's parked pump thread must never share a signal
            // with the next attempt's EReader, or signals dispatch to the wrong (dead) socket.
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

    private void StartReaderPump(EClientSocket client, EReaderSignal signal)
    {
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
        if (ReferenceEquals(_client, client))
        {
            _client = null;
            _readerThread = null;
        }
    }

    public void Disconnect()
    {
        if (_client?.IsConnected() == true)
            _client.eDisconnect();
    }

    public ValueTask DisposeAsync()
    {
        Disconnect();
        return ValueTask.CompletedTask;
    }
}
