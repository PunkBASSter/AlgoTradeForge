using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Archive;
using AlgoTradeForge.HistoryLoader.Infrastructure.Archive;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Archive;

public sealed class BinanceArchiveClientTests
{
    private const string Csv = "1000,1.5,2.5,0.5,2.0,10\n";

    private static byte[] Zip(string entryName, string content)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var entry = zip.CreateEntry(entryName).Open();
            entry.Write(Encoding.UTF8.GetBytes(content));
        }
        return ms.ToArray();
    }

    private static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!.PathAndQuery);
            return Task.FromResult(responder(request));
        }
    }

    private static BinanceArchiveClient CreateClient(StubHandler handler)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("binance-archive").Returns(_ =>
            new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("https://data.binance.vision") });
        var options = Options.Create(new HistoryLoaderOptions());
        return new BinanceArchiveClient(factory, options, NullLogger<BinanceArchiveClient>.Instance);
    }

    [Fact]
    public async Task DownloadMonthly_HappyPath_ReturnsExtractedCsv()
    {
        var zip = Zip("BTCUSDT-1h-2024-03.csv", Csv);
        var handler = new StubHandler(req =>
            req.RequestUri!.AbsolutePath.EndsWith(".CHECKSUM")
                ? new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent($"{Sha256Hex(zip)}  BTCUSDT-1h-2024-03.zip") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) });

        var client = CreateClient(handler);
        await using var stream = await client.DownloadMonthly("futures/um", "klines", "BTCUSDT", "1h", 2024, 3, TestContext.Current.CancellationToken);

        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        Assert.Equal(Csv, await reader.ReadToEndAsync(TestContext.Current.CancellationToken));
        Assert.Contains("/data/futures/um/monthly/klines/BTCUSDT/1h/BTCUSDT-1h-2024-03.zip", handler.Requests);
    }

    [Fact]
    public async Task Download_Returns_Null_On404()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var client = CreateClient(handler);
        Assert.Null(await client.DownloadMonthly("spot", "klines", "BTCUSDT", "1m", 2019, 1, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Download_Throws_OnPersistentChecksumMismatch()
    {
        var zip = Zip("BTCUSDT-metrics-2024-03-01.csv", Csv);
        var handler = new StubHandler(req =>
            req.RequestUri!.AbsolutePath.EndsWith(".CHECKSUM")
                ? new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent("deadbeef  BTCUSDT-metrics-2024-03-01.zip") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) });

        var client = CreateClient(handler);
        await Assert.ThrowsAsync<ArchiveIntegrityException>(() =>
            client.DownloadDaily("futures/um", "metrics", "BTCUSDT", null, new DateOnly(2024, 3, 1), TestContext.Current.CancellationToken));
        // one original attempt + one retry = 2 zip downloads
        Assert.Equal(2, handler.Requests.Count(r => r.EndsWith(".zip")));
    }

    [Fact]
    public async Task DownloadDaily_BuildsMetricsUrl_WithoutIntervalSegment()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var client = CreateClient(handler);
        await client.DownloadDaily("futures/um", "metrics", "BTCUSDT", null, new DateOnly(2024, 3, 1), TestContext.Current.CancellationToken);
        Assert.Contains("/data/futures/um/daily/metrics/BTCUSDT/BTCUSDT-metrics-2024-03-01.zip", handler.Requests);
    }
}
