using System.IO.Compression;
using System.Security.Cryptography;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Archive;
using AlgoTradeForge.Storage.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.Archive;

internal sealed class BinanceArchiveClient : IBinanceArchiveClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<BinanceArchiveClient> _logger;
    private readonly SemaphoreSlim _gate;

    public BinanceArchiveClient(
        IHttpClientFactory httpClientFactory,
        IOptions<HistoryLoaderOptions> options,
        ILogger<BinanceArchiveClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _gate = new SemaphoreSlim(options.Value.Binance.ArchiveDownloadConcurrency);
    }

    public Task<Stream?> DownloadMonthly(string market, string dataset, string symbol, string? interval, int year, int month, CancellationToken ct = default) =>
        Download(market, "monthly", dataset, symbol, interval, $"{year:D4}-{month:D2}", ct);

    public Task<Stream?> DownloadDaily(string market, string dataset, string symbol, string? interval, DateOnly date, CancellationToken ct = default) =>
        Download(market, "daily", dataset, symbol, interval, date.ToString("yyyy-MM-dd"), ct);

    private async Task<Stream?> Download(string market, string period, string dataset, string symbol, string? interval, string stamp, CancellationToken ct)
    {
        var token = interval ?? dataset;
        var dir = interval is null
            ? $"data/{market}/{period}/{dataset}/{symbol}"
            : $"data/{market}/{period}/{dataset}/{symbol}/{interval}";
        var url = $"{dir}/{symbol}-{token}-{stamp}.zip";

        using var _ = await _gate.LockAsync(ct);
        var client = _httpClientFactory.CreateClient("binance-archive");

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var tempZip = await DownloadToTemp(client, url, ct);
            if (tempZip is null)
                return null;

            try
            {
                if (await VerifyChecksum(client, url, tempZip, ct))
                    return ExtractSingleEntry(tempZip); // deletes tempZip in its own finally
            }
            catch
            {
                File.Delete(tempZip);
                throw;
            }

            File.Delete(tempZip);
            _logger.LogWarning("Checksum mismatch for {Url} (attempt {Attempt}/2)", url, attempt + 1);
        }
        throw new ArchiveIntegrityException(url);
    }

    // Transient failures (5xx / network) retry 3× with 1s/2s/4s backoff — spec §5 requires
    // retry-with-backoff and the repo bans new NuGet packages, so no resilience handler.
    private static async Task<string?> DownloadToTemp(HttpClient client, string url, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    return null;
                response.EnsureSuccessStatusCode();

                var tempPath = Path.Combine(Path.GetTempPath(), $"atf-archive-{Guid.NewGuid():N}.zip");
                await using var file = File.Create(tempPath);
                await response.Content.CopyToAsync(file, ct);
                return tempPath;
            }
            catch (HttpRequestException) when (attempt < 3)
            {
                await Task.Delay(TimeSpan.FromSeconds(1 << attempt), ct);
            }
        }
    }

    private static async Task<bool> VerifyChecksum(HttpClient client, string url, string tempZip, CancellationToken ct)
    {
        using var response = await client.GetAsync(url + ".CHECKSUM", ct);
        if (!response.IsSuccessStatusCode)
            return true; // no checksum published — accept the payload
        var text = await response.Content.ReadAsStringAsync(ct);
        var expected = text.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0].Trim();

        await using var file = File.OpenRead(tempZip);
        var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct));
        return string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
    }

    // Extracts the single CSV entry to a DeleteOnClose temp file so the zip can be removed
    // immediately and the returned stream self-cleans on dispose.
    private static Stream ExtractSingleEntry(string tempZip)
    {
        try
        {
            using var zip = ZipFile.OpenRead(tempZip);
            var entry = zip.Entries[0];
            var csvPath = Path.Combine(Path.GetTempPath(), $"atf-archive-{Guid.NewGuid():N}.csv");
            var output = new FileStream(
                csvPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                bufferSize: 81920, FileOptions.DeleteOnClose);
            using (var entryStream = entry.Open())
                entryStream.CopyTo(output);
            output.Position = 0;
            return output;
        }
        finally
        {
            File.Delete(tempZip);
        }
    }
}
