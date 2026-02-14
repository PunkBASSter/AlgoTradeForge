using System.Net;
using System.Text.Json;
using AlgoTradeForge.CandleIngestor;
using AlgoTradeForge.CandleIngestor.DataSourceAdapters;
using Xunit;

namespace AlgoTradeForge.Infrastructure.Tests.CandleIngestion;

public class BinanceAdapterTests
{
    private static readonly AdapterOptions DefaultOptions = new()
    {
        Type = "Binance",
        BaseUrl = "https://api.binance.com",
        RateLimitPerMinute = 1200,
        RequestDelayMs = 0
    };

    private static BinanceAdapter CreateAdapter(HttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri(DefaultOptions.BaseUrl) };
        return new BinanceAdapter(httpClient, DefaultOptions);
    }

    private static string CreateKlinesJson(int count, long startTimeMs, long intervalMs = 60_000)
    {
        var candles = new List<object[]>();
        for (var i = 0; i < count; i++)
        {
            var ts = startTimeMs + i * intervalMs;
            candles.Add(
            [
                ts,                         // open time
                $"{50000 + i}.50",          // open
                $"{50001 + i}.00",          // high
                $"{49999 + i}.00",          // low
                $"{50000 + i}.75",          // close
                $"{100 + i}.123",           // volume
                ts + intervalMs - 1,        // close time
                "1000000.0",                // quote asset volume
                100,                        // number of trades
                "500.0",                    // taker buy base asset volume
                "500000.0",                 // taker buy quote asset volume
                "0"                         // ignore
            ]);
        }
        return JsonSerializer.Serialize(candles);
    }

    [Fact]
    public async Task FetchCandlesAsync_ParsesResponseCorrectly()
    {
        var json = CreateKlinesJson(3, 1704067200000); // 2024-01-01T00:00:00Z
        var handler = new FakeHandler(json);
        var adapter = CreateAdapter(handler);

        var candles = new List<Domain.History.RawCandle>();
        await foreach (var c in adapter.FetchCandlesAsync("BTCUSDT", TimeSpan.FromMinutes(1),
            DateTimeOffset.Parse("2024-01-01T00:00:00+00:00"),
            DateTimeOffset.Parse("2024-01-01T00:03:00+00:00"),
            CancellationToken.None))
        {
            candles.Add(c);
        }

        Assert.Equal(3, candles.Count);
        Assert.Equal(50000.50m, candles[0].Open);
        Assert.Equal(50001.00m, candles[0].High);
        Assert.Equal(49999.00m, candles[0].Low);
        Assert.Equal(50000.75m, candles[0].Close);
        Assert.Equal(100.123m, candles[0].Volume);
    }

    [Fact]
    public async Task FetchCandlesAsync_EmptyResponse_YieldsNothing()
    {
        var handler = new FakeHandler("[]");
        var adapter = CreateAdapter(handler);

        var candles = new List<Domain.History.RawCandle>();
        await foreach (var c in adapter.FetchCandlesAsync("BTCUSDT", TimeSpan.FromMinutes(1),
            DateTimeOffset.Parse("2024-01-01T00:00:00+00:00"),
            DateTimeOffset.Parse("2024-01-01T01:00:00+00:00"),
            CancellationToken.None))
        {
            candles.Add(c);
        }

        Assert.Empty(candles);
    }

    [Fact]
    public void ToIntervalString_MapsCorrectly()
    {
        Assert.Equal("1m", BinanceAdapter.ToIntervalString(TimeSpan.FromMinutes(1)));
        Assert.Equal("5m", BinanceAdapter.ToIntervalString(TimeSpan.FromMinutes(5)));
        Assert.Equal("1h", BinanceAdapter.ToIntervalString(TimeSpan.FromHours(1)));
        Assert.Equal("1d", BinanceAdapter.ToIntervalString(TimeSpan.FromDays(1)));
    }

    [Fact]
    public async Task FetchCandlesAsync_Paginates_WhenResponseHas1000()
    {
        var callCount = 0;
        var handler = new FakeHandler(_ =>
        {
            callCount++;
            if (callCount == 1)
                return CreateKlinesJson(1000, 1704067200000);
            return CreateKlinesJson(5, 1704067200000 + 1000 * 60_000);
        });

        var adapter = CreateAdapter(handler);
        var candles = new List<Domain.History.RawCandle>();
        await foreach (var c in adapter.FetchCandlesAsync("BTCUSDT", TimeSpan.FromMinutes(1),
            DateTimeOffset.Parse("2024-01-01T00:00:00+00:00"),
            DateTimeOffset.Parse("2024-01-02T00:00:00+00:00"),
            CancellationToken.None))
        {
            candles.Add(c);
        }

        Assert.Equal(1005, candles.Count);
        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task FetchCandlesAsync_Http418_ThrowsHttpRequestException()
    {
        var handler = new FakeHandler(HttpStatusCode.RequestedRangeNotSatisfiable, statusCode: (HttpStatusCode)418);
        var adapter = CreateAdapter(handler);

        await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await foreach (var _ in adapter.FetchCandlesAsync("BTCUSDT", TimeSpan.FromMinutes(1),
                DateTimeOffset.Parse("2024-01-01T00:00:00+00:00"),
                DateTimeOffset.Parse("2024-01-01T01:00:00+00:00"),
                CancellationToken.None))
            { }
        });
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, string>? _responseFactory;
        private readonly string? _fixedResponse;
        private readonly HttpStatusCode _statusCode;

        public FakeHandler(string fixedResponse, HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            _fixedResponse = fixedResponse;
            _statusCode = statusCode;
        }

        public FakeHandler(Func<HttpRequestMessage, string> responseFactory)
        {
            _responseFactory = responseFactory;
            _statusCode = HttpStatusCode.OK;
        }

        public FakeHandler(HttpStatusCode _, HttpStatusCode statusCode)
        {
            _statusCode = statusCode;
            _fixedResponse = "";
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var content = _responseFactory?.Invoke(request) ?? _fixedResponse ?? "";
            return Task.FromResult(new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(content)
            });
        }
    }
}
