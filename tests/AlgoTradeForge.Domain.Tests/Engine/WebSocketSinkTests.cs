using System.Net.WebSockets;
using System.Text;
using AlgoTradeForge.Application.Events;
using AlgoTradeForge.Domain.Tests.TestUtilities;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Engine;

public sealed class WebSocketSinkTests
{
    [Fact]
    public async Task Write_SendsEventOverConnectedWebSocket()
    {
        // Arrange
        await using var sink = new WebSocketSink();
        var (serverWs, clientWs) = DuplexStreamPair.CreateLinkedWebSockets();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        sink.Attach(serverWs, cts.Token);

        var eventJson = """{"ts":"2024-01-01T00:00:00Z","sq":1,"_t":"bar","src":"main","d":{}}"""u8;

        // Act
        sink.Write(eventJson.ToArray());

        // Assert — read from client side
        var buffer = new byte[4096];
        var result = await clientWs.ReceiveAsync(buffer.AsMemory(), cts.Token);

        Assert.Equal(WebSocketMessageType.Text, result.MessageType);
        Assert.True(result.EndOfMessage);
        var received = Encoding.UTF8.GetString(buffer, 0, result.Count);
        Assert.Contains("\"_t\":\"bar\"", received);
    }

    [Fact]
    public async Task Write_IsNoOpWhenNoClientConnected()
    {
        // Arrange
        await using var sink = new WebSocketSink();
        var eventJson = """{"ts":"2024-01-01T00:00:00Z","sq":1,"_t":"bar","src":"main","d":{}}"""u8;

        // Act — should not throw
        sink.Write(eventJson.ToArray());

        // Assert
        Assert.False(sink.IsConnected);
    }

    [Fact]
    public async Task Write_HandlesClientDisconnectWithoutThrowing()
    {
        // Arrange
        await using var sink = new WebSocketSink();
        var (serverWs, clientWs) = DuplexStreamPair.CreateLinkedWebSockets();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        sink.Attach(serverWs, cts.Token);

        // Abort the client side to simulate disconnection
        clientWs.Abort();

        var eventJson = """{"ts":"2024-01-01T00:00:00Z","sq":1,"_t":"bar","src":"main","d":{}}"""u8;

        // Act — should not throw even though client disconnected
        sink.Write(eventJson.ToArray());

        // Give the send loop time to process and handle the error
        await Task.Delay(100);
    }

    [Fact]
    public async Task Write_DropsEventsWhenChannelIsFull()
    {
        // Arrange — tiny capacity to force backpressure
        await using var sink = new WebSocketSink(capacity: 2);
        var (serverWs, clientWs) = DuplexStreamPair.CreateLinkedWebSockets();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        sink.Attach(serverWs, cts.Token);

        // Write more events than the channel can hold (without reading them)
        // We need to give the send loop time to potentially process some
        for (int i = 0; i < 10; i++)
        {
            var json = Encoding.UTF8.GetBytes($"{{\"sq\":{i}}}");
            sink.Write(json);
        }

        // Read whatever events made it through
        var received = new List<string>();
        var buffer = new byte[4096];
        while (true)
        {
            var readCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            try
            {
                var result = await clientWs.ReceiveAsync(buffer.AsMemory(), readCts.Token);
                received.Add(Encoding.UTF8.GetString(buffer, 0, result.Count));
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        // Assert — some events may have been dropped due to backpressure
        Assert.True(received.Count > 0, "At least some events should have been delivered");
        Assert.True(received.Count <= 10, "No more than 10 events should be delivered");
    }

    [Fact]
    public async Task SendMessage_RoutesControlMessageThroughChannel()
    {
        // Arrange
        await using var sink = new WebSocketSink();
        var (serverWs, clientWs) = DuplexStreamPair.CreateLinkedWebSockets();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        sink.Attach(serverWs, cts.Token);

        var message = """{"type":"snapshot","sessionActive":true}"""u8;

        // Act — enqueues to channel, send loop delivers
        sink.SendMessage(message.ToArray());

        // Assert
        var buffer = new byte[4096];
        var result = await clientWs.ReceiveAsync(buffer.AsMemory(), cts.Token);
        var received = Encoding.UTF8.GetString(buffer, 0, result.Count);
        Assert.Contains("\"type\":\"snapshot\"", received);
    }

    [Fact]
    public void SendMessage_IsNoOpWhenNoClientConnected()
    {
        // Arrange — no Attach call, so no resources to dispose
        var sink = new WebSocketSink();
        var message = """{"type":"snapshot"}"""u8;

        // Act — should not throw
        sink.SendMessage(message.ToArray());
    }

    [Fact]
    public async Task DetachAsync_AllowsReattachingNewWebSocket()
    {
        // Arrange
        await using var sink = new WebSocketSink();
        var (serverWs1, clientWs1) = DuplexStreamPair.CreateLinkedWebSockets();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        sink.Attach(serverWs1, cts.Token);

        // Verify first connection works
        var msg1 = """{"sq":1}"""u8;
        sink.Write(msg1.ToArray());

        var buffer = new byte[4096];
        var result = await clientWs1.ReceiveAsync(buffer.AsMemory(), cts.Token);
        Assert.True(result.Count > 0);

        // Act — detach first connection, attach a new one
        await sink.DetachAsync();

        var (serverWs2, clientWs2) = DuplexStreamPair.CreateLinkedWebSockets();
        sink.Attach(serverWs2, cts.Token);

        // Verify second connection works
        var msg2 = """{"sq":2}"""u8;
        sink.Write(msg2.ToArray());

        result = await clientWs2.ReceiveAsync(buffer.AsMemory(), cts.Token);
        var received = Encoding.UTF8.GetString(buffer, 0, result.Count);
        Assert.Contains("\"sq\":2", received);
    }
}
