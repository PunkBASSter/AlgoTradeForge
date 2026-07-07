using System.Net;
using System.Text;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Infrastructure.Archive;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Archive;

public sealed class BinanceLoadAssetResolverTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(responder(request));
    }

    private static BinanceLoadAssetResolver CreateResolver(
        StubHandler handler,
        HistoryLoaderOptions? opts = null)
    {
        opts ??= new HistoryLoaderOptions();
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("binance-archive").Returns(_ =>
            new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("https://data.binance.vision") });
        return new BinanceLoadAssetResolver(factory, Options.Create(opts).ToMonitor());
    }

    private static string SpotExchangeInfo(string tickSize) => $$"""
        {
          "symbols": [
            {
              "symbol": "LTCUSDT",
              "filters": [
                { "filterType": "LOT_SIZE", "stepSize": "0.001" },
                { "filterType": "PRICE_FILTER", "tickSize": "{{tickSize}}" }
              ]
            }
          ]
        }
        """;

    [Fact]
    public async Task ConfiguredSymbol_ReturnedAsIs()
    {
        var configured = new AssetCollectionConfig
        {
            Symbol = "BTCUSDT",
            Type = "spot",
            DecimalDigits = 2,
            HistoryStart = new DateOnly(2019, 1, 1),
        };
        var opts = new HistoryLoaderOptions
        {
            Assets = [configured],
        };
        var handler = new StubHandler(_ => throw new InvalidOperationException("should not call HTTP"));
        var resolver = CreateResolver(handler, opts);

        var result = await resolver.Resolve("binance", "BTCUSDT", "spot", TestContext.Current.CancellationToken);

        Assert.Equal("BTCUSDT", result.Symbol);
        Assert.Equal(2, result.DecimalDigits);
        Assert.Equal(new DateOnly(2019, 1, 1), result.HistoryStart);
    }

    [Fact]
    public async Task UnknownSymbol_TickSize_0_01_DecimalDigits_2()
    {
        var handler = new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SpotExchangeInfo("0.01000000"), Encoding.UTF8, "application/json"),
            });
        var resolver = CreateResolver(handler);

        var result = await resolver.Resolve("binance", "LTCUSDT", "spot", TestContext.Current.CancellationToken);

        Assert.Equal("LTCUSDT", result.Symbol);
        Assert.Equal(2, result.DecimalDigits);
        Assert.Equal(new DateOnly(2017, 1, 1), result.HistoryStart);
        Assert.Empty(result.Feeds);
    }

    [Fact]
    public async Task UnknownSymbol_TickSize_1_DecimalDigits_0()
    {
        var handler = new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SpotExchangeInfo("1.00000000"), Encoding.UTF8, "application/json"),
            });
        var resolver = CreateResolver(handler);

        var result = await resolver.Resolve("binance", "SOLUSDT", "spot", TestContext.Current.CancellationToken);

        Assert.Equal(0, result.DecimalDigits);
    }

    [Fact]
    public async Task NonOkResponse_ThrowsInvalidOperationException()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway));
        var resolver = CreateResolver(handler);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            resolver.Resolve("binance", "UNKNOWN", "spot", TestContext.Current.CancellationToken));
    }
}

// Minimal IOptionsMonitor adapter so tests can pass IOptions<T> as IOptionsMonitor<T>.
file static class OptionsExtensions
{
    public static IOptionsMonitor<T> ToMonitor<T>(this IOptions<T> options) where T : class =>
        new OptionsMonitorAdapter<T>(options.Value);

    private sealed class OptionsMonitorAdapter<T>(T value) : IOptionsMonitor<T> where T : class
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
