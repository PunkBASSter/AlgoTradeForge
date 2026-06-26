using IBApi;

namespace AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

// The single IB transport: owns one EClientSocket + EReader pump thread. Plan 1 uses it for
// reqContractDetails; Plan 3 grows IbSession around this exact primitive (tick streaming + shared order
// socket). The wrapper is supplied so the data/order planes can share one callback sink.
internal sealed class IbConnection(IbWrapper wrapper, IbConnectionOptions options) : IAsyncDisposable
{
    private readonly EReaderMonitorSignal _signal = new();
    private EClientSocket? _client;
    private Thread? _readerThread;

    public EClientSocket Client => _client ?? throw new InvalidOperationException("IB connection is not established.");

    // 90 attempts (~3 min): gateway cold start (IBC login + API socket bind) routinely exceeds 60s, and the
    // first socket is often reset once by the 10141 paper-trading disclaimer before the API binds.
    public async Task Connect(int maxAttempts = 90, int retryDelayMs = 2000, CancellationToken ct = default)
    {
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            _client = new EClientSocket(wrapper, _signal);
            try
            {
                _client.eConnect(options.Host, options.Port, options.ClientId);
                if (_client.IsConnected())
                {
                    StartReaderPump(_client);
                    await wrapper.NextValidId.WaitAsync(TimeSpan.FromSeconds(15), ct);
                    return;
                }
            }
            catch (Exception) when (attempt < maxAttempts)
            {
                // transient gateway-cold-start failure; retry below
            }
            await Task.Delay(retryDelayMs, ct);
        }
        throw new TimeoutException($"Could not connect to IB Gateway at {options.Host}:{options.Port}.");
    }

    private void StartReaderPump(EClientSocket client)
    {
        var reader = new EReader(client, _signal);
        reader.Start();
        _readerThread = new Thread(() =>
        {
            while (client.IsConnected())
            {
                _signal.waitForSignal();
                reader.processMsgs();
            }
        }) { IsBackground = true, Name = "ib-ereader" };
        _readerThread.Start();
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
