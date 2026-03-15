using System.Net;
using AlgoTradeForge.HistoryLoader.Infrastructure.Binance;
using AlgoTradeForge.HistoryLoader.Infrastructure.RateLimiting;
using AlgoTradeForge.HistoryLoader.Tests.TestHelpers;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Binance;

public sealed class BinanceRetryHelperTests
{
    private static SourceRateLimiter BuildLimiter() =>
        new(new WeightedRateLimiter(maxWeightPerMinute: 2400, budgetPercent: 100));

    private static int[] ParseInts(string json) =>
        System.Text.Json.JsonSerializer.Deserialize<int[]>(json)!;

    // -------------------------------------------------------------------------
    // 1. Success on first attempt
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FetchWithRetryAsync_Success_ReturnsData()
    {
        int callCount = 0;
        var handler = new FakeHttpHandler
        {
            Handler = _ =>
            {
                callCount++;
                return Task.FromResult(FakeHttpHandler.JsonResponse("[1,2,3]"));
            }
        };

        using var httpClient = new HttpClient(handler);
        var limiter = BuildLimiter();

        var result = await BinanceRetryHelper.FetchWithRetryAsync(
            httpClient, limiter, 0, "https://api.example.com/test", 1,
            ParseInts, CancellationToken.None);

        Assert.Equal([1, 2, 3], result);
        Assert.Equal(1, callCount);
    }

    // -------------------------------------------------------------------------
    // 2. HTTP 429 → retries then succeeds
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FetchWithRetryAsync_Http429_RetriesThenSucceeds()
    {
        int callCount = 0;
        var handler = new FakeHttpHandler
        {
            Handler = _ =>
            {
                callCount++;
                if (callCount <= 2)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
                return Task.FromResult(FakeHttpHandler.JsonResponse("[42]"));
            }
        };

        using var httpClient = new HttpClient(handler);
        var limiter = BuildLimiter();

        var result = await BinanceRetryHelper.FetchWithRetryAsync(
            httpClient, limiter, 0, "https://api.example.com/test", 1,
            ParseInts, CancellationToken.None);

        Assert.Equal([42], result);
        Assert.Equal(3, callCount);
    }

    // -------------------------------------------------------------------------
    // 3. HTTP 429 exhausts retries → throws
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FetchWithRetryAsync_Http429_ExhaustsRetries_Throws()
    {
        var handler = new FakeHttpHandler
        {
            Handler = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests))
        };

        using var httpClient = new HttpClient(handler);
        var limiter = BuildLimiter();

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            BinanceRetryHelper.FetchWithRetryAsync(
                httpClient, limiter, 0, "https://api.example.com/test", 1,
                ParseInts, CancellationToken.None));
    }

    // -------------------------------------------------------------------------
    // 4. HTTP 418 → throws immediately without retry
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FetchWithRetryAsync_Http418_ThrowsImmediately()
    {
        int callCount = 0;
        var handler = new FakeHttpHandler
        {
            Handler = _ =>
            {
                callCount++;
                return Task.FromResult(new HttpResponseMessage((HttpStatusCode)418));
            }
        };

        using var httpClient = new HttpClient(handler);
        var limiter = BuildLimiter();

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            BinanceRetryHelper.FetchWithRetryAsync(
                httpClient, limiter, 0, "https://api.example.com/test", 1,
                ParseInts, CancellationToken.None));

        Assert.Equal(1, callCount);
        Assert.Contains("418", ex.Message);
    }

    // -------------------------------------------------------------------------
    // 5. HTTP 5xx → retries then succeeds
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FetchWithRetryAsync_Http500_RetriesThenSucceeds()
    {
        int callCount = 0;
        var handler = new FakeHttpHandler
        {
            Handler = _ =>
            {
                callCount++;
                if (callCount == 1)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
                return Task.FromResult(FakeHttpHandler.JsonResponse("[99]"));
            }
        };

        using var httpClient = new HttpClient(handler);
        var limiter = BuildLimiter();

        var result = await BinanceRetryHelper.FetchWithRetryAsync(
            httpClient, limiter, 0, "https://api.example.com/test", 1,
            ParseInts, CancellationToken.None);

        Assert.Equal([99], result);
        Assert.Equal(2, callCount);
    }

    // -------------------------------------------------------------------------
    // 6. HTTP 5xx exhausts retries → throws
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FetchWithRetryAsync_Http500_ExhaustsRetries_Throws()
    {
        var handler = new FakeHttpHandler
        {
            Handler = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError))
        };

        using var httpClient = new HttpClient(handler);
        var limiter = BuildLimiter();

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            BinanceRetryHelper.FetchWithRetryAsync(
                httpClient, limiter, 0, "https://api.example.com/test", 1,
                ParseInts, CancellationToken.None));
    }

    // -------------------------------------------------------------------------
    // 7. HTTP 400 → throws on first attempt (no retry for client errors)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FetchWithRetryAsync_Http400_ThrowsWithoutRetry()
    {
        int callCount = 0;
        var handler = new FakeHttpHandler
        {
            Handler = _ =>
            {
                callCount++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest));
            }
        };

        using var httpClient = new HttpClient(handler);
        var limiter = BuildLimiter();

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            BinanceRetryHelper.FetchWithRetryAsync(
                httpClient, limiter, 0, "https://api.example.com/test", 1,
                ParseInts, CancellationToken.None));

        Assert.Equal(1, callCount);
    }

    // -------------------------------------------------------------------------
    // 8. Cancellation is respected
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FetchWithRetryAsync_Cancellation_Throws()
    {
        var handler = new FakeHttpHandler
        {
            Handler = _ => Task.FromResult(FakeHttpHandler.JsonResponse("[1]"))
        };

        using var httpClient = new HttpClient(handler);
        var limiter = BuildLimiter();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            BinanceRetryHelper.FetchWithRetryAsync(
                httpClient, limiter, 0, "https://api.example.com/test", 1,
                ParseInts, cts.Token));
    }
}
