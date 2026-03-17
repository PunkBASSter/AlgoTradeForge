using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AlgoTradeForge.WebApi.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace AlgoTradeForge.WebApi.Tests.Endpoints;

public class LiveSessionDataEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly HttpClient _client;

    public LiveSessionDataEndpointTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GetSessionData_NonExistent_Returns404()
    {
        var response = await _client.GetAsync($"/api/live/sessions/{Guid.NewGuid()}/data", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetSessionData_ResponseShape_HasRequiredFields()
    {
        // Arrange — start a live session so we can query its data
        // In integration test context (no real exchange), starting will fail.
        // Instead, verify the 404 response shape for non-existent session.
        var id = Guid.NewGuid();
        var response = await _client.GetAsync($"/api/live/sessions/{id}/data", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var error = JsonSerializer.Deserialize<JsonElement>(body, JsonOptions);
        Assert.True(error.TryGetProperty("error", out _));
    }

    [Fact]
    public void LiveSessionDataResponse_Candle_HasOhlcvFields()
    {
        // Verify the CandleResponse record has all required OHLCV fields
        var candle = new CandleResponse(
            Time: 1709683200000,
            Open: 65000.50m,
            High: 66000.00m,
            Low: 64500.25m,
            Close: 65500.75m,
            Volume: 12345);

        Assert.Equal(1709683200000, candle.Time);
        Assert.Equal(65000.50m, candle.Open);
        Assert.Equal(66000.00m, candle.High);
        Assert.Equal(64500.25m, candle.Low);
        Assert.Equal(65500.75m, candle.Close);
        Assert.Equal(12345, candle.Volume);
    }

    [Fact]
    public void LiveSessionDataResponse_LastBar_HasSymbolTimeFrameAndOhlcv()
    {
        // Verify LastBarResponse carries subscription context (symbol + timeframe) and OHLCV
        var lastBar = new LastBarResponse(
            Symbol: "BTCUSDT",
            TimeFrame: "00:01:00",
            Time: 1709683200000,
            Open: 65000.00m,
            High: 66000.00m,
            Low: 64000.00m,
            Close: 65500.00m,
            Volume: 999);

        Assert.Equal("BTCUSDT", lastBar.Symbol);
        Assert.Equal("00:01:00", lastBar.TimeFrame);
        Assert.Equal(1709683200000, lastBar.Time);
        Assert.Equal(65000.00m, lastBar.Open);
        Assert.Equal(66000.00m, lastBar.High);
        Assert.Equal(64000.00m, lastBar.Low);
        Assert.Equal(65500.00m, lastBar.Close);
        Assert.Equal(999, lastBar.Volume);
    }

    [Fact]
    public void LiveSessionDataResponse_Fill_HasRequiredFields()
    {
        var fill = new FillResponse(
            OrderId: 42,
            Timestamp: "2026-03-06T12:00:00Z",
            Price: 65000.00m,
            Quantity: 0.5m,
            Side: "Buy",
            Commission: 6.50m);

        Assert.Equal(42, fill.OrderId);
        Assert.Equal("2026-03-06T12:00:00Z", fill.Timestamp);
        Assert.Equal(65000.00m, fill.Price);
        Assert.Equal(0.5m, fill.Quantity);
        Assert.Equal("Buy", fill.Side);
        Assert.Equal(6.50m, fill.Commission);
    }

    [Fact]
    public void LiveSessionDataResponse_Account_HasCashAndPositions()
    {
        var position = new PositionResponse(
            Symbol: "BTCUSDT",
            Quantity: 1.5m,
            AverageEntryPrice: 64000.00m,
            RealizedPnl: 500.00m);

        var account = new AccountResponse(
            InitialCash: 100000m,
            Cash: 96000m,
            ExchangeBalance: 100000m,
            Positions: [position]);

        Assert.Equal(100000m, account.InitialCash);
        Assert.Equal(96000m, account.Cash);
        Assert.Single(account.Positions);
        Assert.Equal("BTCUSDT", account.Positions[0].Symbol);
        Assert.Equal(1.5m, account.Positions[0].Quantity);
        Assert.Equal(64000.00m, account.Positions[0].AverageEntryPrice);
        Assert.Equal(500.00m, account.Positions[0].RealizedPnl);
    }

    [Fact]
    public void LiveSessionDataResponse_PendingOrder_HasRequiredFields()
    {
        var order = new PendingOrderResponse(
            Id: 7,
            Side: "Buy",
            Type: "Limit",
            Quantity: 0.1m,
            LimitPrice: 63000.00m,
            StopPrice: null);

        Assert.Equal(7, order.Id);
        Assert.Equal("Buy", order.Side);
        Assert.Equal("Limit", order.Type);
        Assert.Equal(0.1m, order.Quantity);
        Assert.Equal(63000.00m, order.LimitPrice);
        Assert.Null(order.StopPrice);
    }

    [Fact]
    public void LiveSessionDataResponse_ExchangeTrade_HasCommissionAsset()
    {
        var trade = new ExchangeTradeResponse(
            OrderId: 99,
            Timestamp: "2026-03-05T18:30:00Z",
            Price: 64500.00m,
            Quantity: 0.1m,
            Side: "Sell",
            Commission: 0.00000827m,
            CommissionAsset: "BNB");

        Assert.Equal(99, trade.OrderId);
        Assert.Equal("2026-03-05T18:30:00Z", trade.Timestamp);
        Assert.Equal(64500.00m, trade.Price);
        Assert.Equal(0.1m, trade.Quantity);
        Assert.Equal("Sell", trade.Side);
        Assert.Equal(0.00000827m, trade.Commission);
        Assert.Equal("BNB", trade.CommissionAsset);
    }

    [Fact]
    public void LiveSessionDataResponse_FullAssembly_SerializesCorrectly()
    {
        // Build a complete response and verify JSON round-trip
        var response = new LiveSessionDataResponse
        {
            Candles = [new CandleResponse(1709683200000, 65000m, 66000m, 64000m, 65500m, 100)],
            Fills = [new FillResponse(1, "2026-03-06T12:00:00Z", 65000m, 0.5m, "Buy", 6.5m)],
            PendingOrders = [new PendingOrderResponse(2, "Sell", "Limit", 0.3m, 67000m, null)],
            Account = new AccountResponse(100000m, 95000m, 100000m, [new PositionResponse("BTCUSDT", 0.5m, 65000m, 0m)]),
            TimeFrame = "00:01:00",
            LastBars = [new LastBarResponse("BTCUSDT", "00:01:00", 1709683200000, 65000m, 66000m, 64000m, 65500m, 100)],
            ExchangeTrades = [new ExchangeTradeResponse(99, "2026-03-05T18:30:00Z", 64500m, 0.1m, "Sell", 3.2m, "USDT")],
        };

        var json = JsonSerializer.Serialize(response, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<LiveSessionDataResponse>(json, JsonOptions);

        Assert.NotNull(deserialized);
        Assert.Single(deserialized.Candles);
        Assert.Single(deserialized.Fills);
        Assert.Single(deserialized.PendingOrders);
        Assert.Single(deserialized.Account.Positions);
        Assert.Equal("00:01:00", deserialized.TimeFrame);
        Assert.Single(deserialized.LastBars);

        // Verify LastBars carry OHLCV of the last bar per subscription
        var lastBar = deserialized.LastBars[0];
        Assert.Equal("BTCUSDT", lastBar.Symbol);
        Assert.Equal("00:01:00", lastBar.TimeFrame);
        Assert.Equal(65000m, lastBar.Open);
        Assert.Equal(66000m, lastBar.High);
        Assert.Equal(64000m, lastBar.Low);
        Assert.Equal(65500m, lastBar.Close);
        Assert.Equal(100, lastBar.Volume);

        // Verify ExchangeTrades round-trip
        Assert.Single(deserialized.ExchangeTrades);
        var trade = deserialized.ExchangeTrades[0];
        Assert.Equal(99, trade.OrderId);
        Assert.Equal("2026-03-05T18:30:00Z", trade.Timestamp);
        Assert.Equal(64500m, trade.Price);
        Assert.Equal(0.1m, trade.Quantity);
        Assert.Equal("Sell", trade.Side);
        Assert.Equal(3.2m, trade.Commission);
        Assert.Equal("USDT", trade.CommissionAsset);
    }
}
