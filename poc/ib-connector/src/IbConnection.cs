using IBApi;

namespace IbPoc;

internal sealed class IbConnection : IAsyncDisposable
{
    private readonly DemoWrapper _wrapper;
    private readonly string _host;
    private readonly int _port;
    private readonly int _clientId;
    private readonly EReaderMonitorSignal _signal = new();
    private EClientSocket? _client;
    private Thread? _readerThread;

    public IbConnection(DemoWrapper wrapper, string host, int port, int clientId)
    {
        _wrapper = wrapper;
        _host = host;
        _port = port;
        _clientId = clientId;
    }

    public EClientSocket Client => _client ?? throw new InvalidOperationException("not connected");

    public async Task ConnectAsync(int maxAttempts = 90, int retryDelayMs = 2000, CancellationToken ct = default)
    {
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            _client = new EClientSocket(_wrapper, _signal);
            try
            {
                Log.Line($"eConnect {_host}:{_port} clientId={_clientId} (attempt {attempt}/{maxAttempts})");
                _client.eConnect(_host, _port, _clientId);
                if (_client.IsConnected())
                {
                    StartReaderPump(_client);
                    var orderId = await _wrapper.NextValidIdAsync.WaitAsync(TimeSpan.FromSeconds(15), ct);
                    Log.Line($"connected; first orderId={orderId}");
                    return;
                }
            }
            catch (Exception e)
            {
                Log.Line($"connect attempt failed: {e.Message}");
            }
            await Task.Delay(retryDelayMs, ct);
        }
        throw new TimeoutException($"could not connect to IB Gateway at {_host}:{_port}");
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
        {
            Log.Line("eDisconnect");
            _client.eDisconnect();
        }
    }

    public ValueTask DisposeAsync()
    {
        Disconnect();
        return ValueTask.CompletedTask;
    }
}
