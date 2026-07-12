using System.Net;
using System.Text;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Application.Index;
using AlgoTradeForge.HistoryLoader.Infrastructure.Binance;
using AlgoTradeForge.HistoryLoader.Infrastructure.Index;
using AlgoTradeForge.HistoryLoader.Tests.TestHelpers;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Binance;

public sealed class BinanceInstrumentMetaProviderTests : IAsyncLifetime, IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("atf-meta-").FullName;
    private SqliteHistoryIndex _index = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string FuturesJson = """
        {"symbols":[{"symbol":"BTCUSDT","status":"TRADING",
          "filters":[{"filterType":"PRICE_FILTER","tickSize":"0.10000000"},
                     {"filterType":"LOT_SIZE","stepSize":"0.00100000"}]}]}
        """;

    private const string SpotJson = """
        {"symbols":[{"symbol":"BTCUSDT","status":"TRADING",
          "filters":[{"filterType":"PRICE_FILTER","tickSize":"0.01000000"},
                     {"filterType":"LOT_SIZE","stepSize":"0.00001000"}]}]}
        """;

    public async ValueTask InitializeAsync()
    {
        var init = new HistoryIndexInitializer(Path.Combine(_dir, "idx.sqlite"));
        await init.EnsureCreated(Ct);
        _index = new SqliteHistoryIndex(init, init.ConnectionString + ";Pooling=False");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private BinanceInstrumentMetaProvider CreateProvider(FakeHttpHandler handler, TestClock? clock = null)
    {
        clock ??= new TestClock(DateTimeOffset.UtcNow);
        var opts = new HistoryLoaderOptions
        {
            Binance = new BinanceOptions
            {
                FuturesBaseUrl = "https://fapi.binance.com",
                SpotBaseUrl = "https://api.binance.com",
            }
        };
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("binance-meta").Returns(_ =>
            new HttpClient(handler, disposeHandler: false));
        var monitor = Options.Create(opts).ToMonitor();
        return new BinanceInstrumentMetaProvider(factory, _index, monitor, clock,
            NullLogger<BinanceInstrumentMetaProvider>.Instance);
    }

    [Fact]
    public async Task EnsureFresh_UpsertsRowsFromPriceAndLotFilters()
    {
        var handler = new FakeHttpHandler();
        handler.Handler = req =>
        {
            var json = req.RequestUri!.AbsolutePath.StartsWith("/fapi") ? FuturesJson : SpotJson;
            return Task.FromResult(FakeHttpHandler.JsonResponse(json));
        };

        var provider = CreateProvider(handler);
        await provider.EnsureFresh("binance", Ct);

        var rows = await _index.ListInstrumentMeta("binance", Ct);
        // futures BTCUSDT → BTCUSDT_perp with tickSize=0.10 → 1 decimal
        var futures = rows.Single(r => r.Dir == "BTCUSDT_perp");
        Assert.Equal(1, futures.PriceDecimals);
        Assert.Equal(3, futures.QtyDecimals);  // stepSize 0.00100000 → 3

        // spot BTCUSDT → BTCUSDT (bare) with tickSize=0.01 → 2 decimals
        var spot = rows.Single(r => r.Dir == "BTCUSDT");
        Assert.Equal(2, spot.PriceDecimals);
        Assert.Equal(5, spot.QtyDecimals);     // stepSize 0.00001000 → 5
    }

    [Fact]
    public async Task EnsureFresh_WithinTtl_DoesNotRefetch()
    {
        var callCount = 0;
        var handler = new FakeHttpHandler();
        handler.Handler = req =>
        {
            callCount++;
            var json = req.RequestUri!.AbsolutePath.StartsWith("/fapi") ? FuturesJson : SpotJson;
            return Task.FromResult(FakeHttpHandler.JsonResponse(json));
        };

        var clock = new TestClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var provider = CreateProvider(handler, clock);

        await provider.EnsureFresh("binance", Ct);
        Assert.Equal(2, callCount);  // one futures + one spot

        // Second call within the 24h TTL must not hit HTTP again.
        await provider.EnsureFresh("binance", Ct);
        Assert.Equal(2, callCount);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
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
