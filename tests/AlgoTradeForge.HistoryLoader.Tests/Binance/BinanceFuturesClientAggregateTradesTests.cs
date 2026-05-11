using System.Text;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Infrastructure.Binance;
using AlgoTradeForge.HistoryLoader.Infrastructure.RateLimiting;
using AlgoTradeForge.HistoryLoader.Tests.TestHelpers;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Binance;

/// <summary>
/// Phase 2a (H1): FetchAggTradesAsync uses time-bounds for the first page and fromId-bounds
/// for subsequent pages. The fromId path is the only correct way to walk past a single-ms
/// burst of &gt;1000 trades — the previous <c>cursor = lastTs + 1</c> approach silently
/// dropped overflow trades in that ms.
/// </summary>
public sealed class BinanceFuturesClientAggregateTradesTests
{
    private static BinanceFuturesClient BuildClient(FakeHttpHandler handler) =>
        new(new HttpClient(handler),
            new BinanceOptions { RequestDelayMs = 0 },
            new SourceRateLimiter(new WeightedRateLimiter(maxWeightPerMinute: 2400, budgetPercent: 100)));

    /// <summary>
    /// Builds a JSON array of aggTrade objects matching the Binance
    /// <c>GET /fapi/v1/aggTrades</c> response shape.
    /// </summary>
    private static string BuildAggTradeJson(
        params (long aggId, string price, string qty, long timestampMs, bool isBuyerMaker)[] trades)
    {
        var sb = new StringBuilder();
        sb.Append('[');
        for (int i = 0; i < trades.Length; i++)
        {
            if (i > 0) sb.Append(',');
            var t = trades[i];
            sb.Append("{")
              .Append($"\"a\":{t.aggId},")
              .Append($"\"p\":\"{t.price}\",")
              .Append($"\"q\":\"{t.qty}\",")
              .Append("\"f\":1,\"l\":1,")
              .Append($"\"T\":{t.timestampMs},")
              .Append($"\"m\":{(t.isBuyerMaker ? "true" : "false")},")
              .Append("\"M\":true")
              .Append("}");
        }
        sb.Append(']');
        return sb.ToString();
    }

    // -------------------------------------------------------------------------
    // 1. Burst saturation — 1000+ trades sharing a single millisecond
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FetchAggTradesAsync_BurstSaturation_UsesFromIdAndYieldsAllTrades()
    {
        // Page 1: 1000 trades all at T=1_700_000_000_000, aggIds 100..1099.
        // Page 2: 500 trades still at the same T, aggIds 1100..1599.
        // Page 3: empty (test termination).
        // Without fromId pagination, the second request would advance cursor to T+1 and
        // skip aggIds 1100..1599 entirely.
        const long burstMs = 1_700_000_000_000L;

        var page1 = BuildAggTradeJson(
            Enumerable.Range(0, 1000)
                .Select(i => ((long)(100 + i), "50000.00", "0.001", burstMs, false))
                .ToArray());

        var page2 = BuildAggTradeJson(
            Enumerable.Range(0, 500)
                .Select(i => ((long)(1100 + i), "50001.00", "0.002", burstMs, true))
                .ToArray());

        int requestCount = 0;
        var capturedUrls = new List<string>();

        var handler = new FakeHttpHandler
        {
            Handler = req =>
            {
                requestCount++;
                capturedUrls.Add(req.RequestUri?.ToString() ?? "");
                return Task.FromResult(FakeHttpHandler.JsonResponse(requestCount switch
                {
                    1 => page1,
                    2 => page2,
                    _ => "[]",
                }));
            }
        };

        var client = BuildClient(handler);
        var records = await client
            .FetchAggTradesAsync("BTCUSDT", burstMs, burstMs + 60_000, TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1500, records.Count);
        Assert.Equal(2, requestCount);

        // First request: time-bounded.
        Assert.Contains("startTime=", capturedUrls[0]);
        Assert.Contains("endTime=",   capturedUrls[0]);
        Assert.DoesNotContain("fromId=", capturedUrls[0]);

        // Second request: id-bounded with fromId = lastAggId + 1 = 1100.
        Assert.Contains("fromId=1100", capturedUrls[1]);
        Assert.DoesNotContain("startTime=", capturedUrls[1]);
        Assert.DoesNotContain("endTime=",   capturedUrls[1]);

        // First and last record sanity.
        Assert.Equal(100.0,  records[0].Values[3]);
        Assert.Equal(1599.0, records[^1].Values[3]);
    }

    // -------------------------------------------------------------------------
    // 2. Partial first page — terminates without second request
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FetchAggTradesAsync_PartialFirstPage_NoSecondRequest()
    {
        var json = BuildAggTradeJson(
            (100L, "50000.00", "0.001", 1_700_000_000_000L, false),
            (101L, "50001.00", "0.001", 1_700_000_001_000L, true));

        int requestCount = 0;
        var handler = new FakeHttpHandler
        {
            Handler = _ =>
            {
                requestCount++;
                return Task.FromResult(FakeHttpHandler.JsonResponse(json));
            }
        };

        var client = BuildClient(handler);
        var records = await client
            .FetchAggTradesAsync("BTCUSDT", 1_700_000_000_000L, 1_700_000_060_000L, TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, records.Count);
        Assert.Equal(1, requestCount);
    }

    // -------------------------------------------------------------------------
    // 3. fromId-bounded path returns trades past toMs — must trim client-side
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FetchAggTradesAsync_IdBoundedPagePastToMs_TrimsClientSide()
    {
        // Page 1 fills the limit so we move to fromId pagination.
        // Page 2 returns trades whose timestamps cross toMs — only the in-range ones must
        // surface (Binance's id-bounded path ignores endTime).
        const long startMs = 1_700_000_000_000L;
        const long toMs    = 1_700_000_000_999L;

        var page1 = BuildAggTradeJson(
            Enumerable.Range(0, 1000)
                .Select(i => ((long)(500 + i), "50000.00", "0.001", startMs + i, false))
                .ToArray());

        // Page 2: aggIds 1500..1502, timestamps straddle toMs (998, 999, 1000).
        var page2 = BuildAggTradeJson(
            (1500L, "50000.00", "0.001", startMs + 998, false),  // in range
            (1501L, "50001.00", "0.001", startMs + 999, false),  // exactly toMs
            (1502L, "50002.00", "0.001", startMs + 1000, false));// past toMs — must NOT yield

        int requestCount = 0;
        var handler = new FakeHttpHandler
        {
            Handler = _ =>
            {
                requestCount++;
                return Task.FromResult(FakeHttpHandler.JsonResponse(requestCount == 1 ? page1 : page2));
            }
        };

        var client = BuildClient(handler);
        var records = await client
            .FetchAggTradesAsync("BTCUSDT", startMs, toMs, TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        // All 1000 from page1 (T=startMs..startMs+999) plus first 2 from page2 (998, 999).
        // Note: page1 already has T=startMs+998 and T=startMs+999, so we DO double-yield by
        // aggId here — the writer's agg_id dedup filters duplicates downstream. The contract
        // for THIS layer is "yield everything ≤ toMs", which is what we're verifying.
        Assert.Equal(1002, records.Count);
        Assert.DoesNotContain(records, r => r.TimestampMs > toMs);
    }

    // -------------------------------------------------------------------------
    // 4. Id-reset sanity — bail if subsequent page first aggId <= prev last
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FetchAggTradesAsync_AggIdRegression_BailsLoudly()
    {
        // Page 1: 1000 aggIds 100..1099. Page 2: aggIds 50..149 (regression — id reset).
        // The collector must yield page 1 records and then bail without yielding page 2.
        const long ts = 1_700_000_000_000L;

        var page1 = BuildAggTradeJson(
            Enumerable.Range(0, 1000)
                .Select(i => ((long)(100 + i), "50000.00", "0.001", ts + i, false))
                .ToArray());

        var page2 = BuildAggTradeJson(
            Enumerable.Range(0, 100)
                .Select(i => ((long)(50 + i), "50000.00", "0.001", ts + 1100 + i, false))
                .ToArray());

        int requestCount = 0;
        var handler = new FakeHttpHandler
        {
            Handler = _ =>
            {
                requestCount++;
                return Task.FromResult(FakeHttpHandler.JsonResponse(requestCount == 1 ? page1 : page2));
            }
        };

        var client = BuildClient(handler);
        var records = await client
            .FetchAggTradesAsync("BTCUSDT", ts, ts + 60_000, TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1000, records.Count);
        Assert.Equal(1099.0, records[^1].Values[3]);  // last yielded is page-1 tail
        Assert.Equal(2, requestCount);
    }

    // -------------------------------------------------------------------------
    // 5. Empty first response — no second request, no records
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FetchAggTradesAsync_EmptyFirstPage_YieldsNothing()
    {
        int requestCount = 0;
        var handler = new FakeHttpHandler
        {
            Handler = _ =>
            {
                requestCount++;
                return Task.FromResult(FakeHttpHandler.JsonResponse("[]"));
            }
        };

        var client = BuildClient(handler);
        var records = await client
            .FetchAggTradesAsync("BTCUSDT", 1_700_000_000_000L, 1_700_000_060_000L, TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Empty(records);
        Assert.Equal(1, requestCount);
    }
}
