using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AlgoTradeForge.Application.Debug;
using AlgoTradeForge.WebApi.Contracts;
using AlgoTradeForge.WebApi.Tests.Infrastructure;

namespace AlgoTradeForge.WebApi.Tests.Endpoints;

[Collection("Api")]
public sealed class DebugEndpointsApiTests : ApiTestBase
{
    private readonly AlgoTradeForgeApiFactory _factory;

    public DebugEndpointsApiTests(AlgoTradeForgeApiFactory factory) : base(factory)
    {
        _factory = factory;
    }

    private async Task<DebugSessionDto> CreateSessionAsync()
    {
        // Wait to ensure unique second-level timestamp for event log folder path
        await Task.Delay(1100);

        var request = MakeDebugSessionRequest();
        var response = await Client.PostAsJsonAsync("/api/debug-sessions", request, Json);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            $"Expected 201 but got {(int)response.StatusCode}: {body[..Math.Min(500, body.Length)]}");
        return JsonSerializer.Deserialize<DebugSessionDto>(body, Json)!;
    }

    private async Task CleanupSessionAsync(Guid sessionId)
    {
        try { await Client.DeleteAsync($"/api/debug-sessions/{sessionId}"); }
        catch { /* best-effort cleanup */ }
    }

    // ── REST happy paths ─────────────────────────────────────────────

    [Fact]
    public async Task Post_ValidRequest_Returns201WithSession()
    {
        var session = await CreateSessionAsync();

        Assert.NotEqual(Guid.Empty, session.SessionId);
        Assert.Equal("BTCUSDT", session.AssetName);
        Assert.Equal("ZigZagBreakout", session.StrategyName);

        await CleanupSessionAsync(session.SessionId);
    }

    [Fact]
    public async Task GetStatus_ExistingSession_Returns200()
    {
        var session = await CreateSessionAsync();

        var response = await Client.GetAsync($"/api/debug-sessions/{session.SessionId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var status = await response.Content.ReadFromJsonAsync<DebugSessionStatusDto>(Json);
        Assert.NotNull(status);
        Assert.Equal(session.SessionId, status.SessionId);

        await CleanupSessionAsync(session.SessionId);
    }

    [Fact]
    public async Task Delete_ExistingSession_Returns204()
    {
        var session = await CreateSessionAsync();

        var response = await Client.DeleteAsync($"/api/debug-sessions/{session.SessionId}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    // ── REST negative tests ──────────────────────────────────────────

    [Fact]
    public async Task GetStatus_RandomGuid_Returns404()
    {
        var response = await Client.GetAsync($"/api/debug-sessions/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Delete_RandomGuid_Returns404()
    {
        var response = await Client.DeleteAsync($"/api/debug-sessions/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Post_InvalidStrategy_Returns400()
    {
        var request = new StartDebugSessionRequest
        {
            AssetName = "BTCUSDT",
            Exchange = "Binance",
            StrategyName = "NonExistentStrategy",
            InitialCash = 10_000m,
            StartTime = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
            EndTime = new DateTimeOffset(2025, 1, 5, 0, 0, 0, TimeSpan.Zero),
            TimeFrame = "01:00:00",
        };

        var response = await Client.PostAsJsonAsync("/api/debug-sessions", request, Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── WebSocket test ───────────────────────────────────────────────

    [Fact]
    public async Task WebSocket_StepCommand_ReceivesSnapshot()
    {
        var session = await CreateSessionAsync();

        // Connect WebSocket to the test server
        var wsClient = _factory.Server.CreateWebSocketClient();
        var wsUri = new Uri(_factory.Server.BaseAddress, $"api/debug-sessions/{session.SessionId}/ws");
        using var ws = await wsClient.ConnectAsync(wsUri, CancellationToken.None);

        // Send a "next" command
        var commandJson = JsonSerializer.SerializeToUtf8Bytes(
            new { command = "next" }, Json);
        await ws.SendAsync(commandJson, WebSocketMessageType.Text, true, CancellationToken.None);

        // Read messages until we get a snapshot (event bus messages may arrive first)
        var buffer = new byte[4096];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        JsonElement? snapshotRoot = null;
        while (!cts.Token.IsCancellationRequested)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(buffer, cts.Token);
                ms.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            if (result.MessageType != WebSocketMessageType.Text)
                continue;

            var text = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("type", out var typeEl) &&
                typeEl.GetString() == "snapshot")
            {
                snapshotRoot = doc.RootElement.Clone();
                break;
            }
        }

        Assert.NotNull(snapshotRoot);
        Assert.True(snapshotRoot.Value.TryGetProperty("sequenceNumber", out _));
        Assert.True(snapshotRoot.Value.TryGetProperty("timestampMs", out _));

        // Close WebSocket gracefully
        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);

        await CleanupSessionAsync(session.SessionId);
    }

    [Fact]
    public async Task WebSocket_NonExistentSession_RejectsUpgrade()
    {
        var wsClient = _factory.Server.CreateWebSocketClient();
        var wsUri = new Uri(_factory.Server.BaseAddress, $"api/debug-sessions/{Guid.NewGuid()}/ws");

        // The test server should reject the upgrade with a non-101 status
        await Assert.ThrowsAnyAsync<Exception>(
            () => wsClient.ConnectAsync(wsUri, CancellationToken.None));
    }
}
