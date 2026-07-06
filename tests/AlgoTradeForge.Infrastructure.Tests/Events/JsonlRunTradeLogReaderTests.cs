using AlgoTradeForge.Infrastructure.Events;
using AlgoTradeForge.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AlgoTradeForge.Infrastructure.Tests.Events;

public sealed class JsonlRunTradeLogReaderTests : IDisposable
{
    private readonly string _runDir =
        Path.Combine(Path.GetTempPath(), $"TradeLogReader_{Guid.NewGuid():N}");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public JsonlRunTradeLogReaderTests() => Directory.CreateDirectory(_runDir);

    public void Dispose()
    {
        if (Directory.Exists(_runDir))
            Directory.Delete(_runDir, recursive: true);
    }

    private JsonlRunTradeLogReader CreateReader() =>
        new(new LocalFileStorage(), NullLogger<JsonlRunTradeLogReader>.Instance);

    private void WriteEvents(params string[] lines) =>
        File.WriteAllLines(Path.Combine(_runDir, "events.jsonl"), lines);

    [Fact]
    public async Task RegistryTrade_EntryTpExit_PairsWithSlTpEnrichment()
    {
        // Short entry via group; the registry emits entryFilled/slPlaced/tpPlaced BEFORE the
        // engine flushes the entry ord.fill (real event ordering) → SL/TP must still attach.
        WriteEvents(
            """{"ts":"2026-01-23T14:45:00+00:00","sq":2,"_t":"grp","src":"trade-registry","d":{"groupId":1,"assetName":"NFLX","transition":"entrySubmitted","orderId":-1,"price":8512,"quantity":54.5}}""",
            """{"ts":"2026-01-23T14:55:00+00:00","sq":3,"_t":"grp","src":"trade-registry","d":{"groupId":1,"assetName":"NFLX","transition":"entryFilled","orderId":-1,"price":8512,"quantity":54.5}}""",
            """{"ts":"2026-01-23T14:55:00+00:00","sq":4,"_t":"grp","src":"trade-registry","d":{"groupId":1,"assetName":"NFLX","transition":"slPlaced","orderId":-2,"price":8600,"quantity":54.5}}""",
            """{"ts":"2026-01-23T14:55:00+00:00","sq":5,"_t":"grp","src":"trade-registry","d":{"groupId":1,"assetName":"NFLX","transition":"tpPlaced","orderId":-3,"price":8400,"quantity":54.5}}""",
            """{"ts":"2026-01-23T14:55:00+00:00","sq":6,"_t":"ord.fill","src":"engine","d":{"orderId":-1,"assetName":"NFLX","side":"sell","price":8512,"quantity":54.5,"commission":463}}""",
            """{"ts":"2026-01-23T16:10:00+00:00","sq":7,"_t":"ord.fill","src":"engine","d":{"orderId":-3,"assetName":"NFLX","side":"buy","price":8400,"quantity":54.5,"commission":457}}""",
            """{"ts":"2026-01-23T16:10:00+00:00","sq":8,"_t":"grp","src":"trade-registry","d":{"groupId":1,"assetName":"NFLX","transition":"tpFilled","orderId":-3,"price":8400,"quantity":54.5}}""");

        var trades = await CreateReader().Read(_runDir, Ct);

        var t = Assert.Single(trades);
        Assert.Equal("sell", t.Side);
        Assert.Equal(8512, t.EntryPrice);
        Assert.Equal(8400, t.ExitPrice);
        Assert.Equal(54.5m, t.Quantity);
        Assert.Equal(8600, t.StopLossPrice);
        Assert.Equal(8400, t.TakeProfitPrice);
        Assert.Equal(463 + 457, t.Commission);
        // Short: (entry - exit) × qty = (8512-8400) × 54.5 = 6104 ticks gross, minus commissions.
        Assert.Equal(6104 - 920, t.Pnl);
        Assert.Equal(new DateTimeOffset(2026, 1, 23, 14, 55, 0, TimeSpan.Zero), t.EntryTime);
        Assert.Equal(new DateTimeOffset(2026, 1, 23, 16, 10, 0, TimeSpan.Zero), t.ExitTime);
    }

    [Fact]
    public async Task RawFlattenExit_PairsWithoutGroupTransitions()
    {
        // Long entry via group, but the exit is a raw limit fill (manual flatten) — no
        // tpFilled/slFilled transition. Position returning to zero must still close the trade.
        WriteEvents(
            """{"ts":"2026-02-02T15:00:00+00:00","sq":2,"_t":"ord.fill","src":"engine","d":{"orderId":-1,"assetName":"NFLX","side":"buy","price":9000,"quantity":10,"commission":90}}""",
            """{"ts":"2026-02-02T19:55:00+00:00","sq":3,"_t":"ord.fill","src":"engine","d":{"orderId":7,"assetName":"NFLX","side":"sell","price":9100,"quantity":10,"commission":91}}""");

        var trades = await CreateReader().Read(_runDir, Ct);

        var t = Assert.Single(trades);
        Assert.Equal("buy", t.Side);
        Assert.Equal(9000, t.EntryPrice);
        Assert.Equal(9100, t.ExitPrice);
        // Long: (9100-9000) × 10 = 1000 gross − 181 commission.
        Assert.Equal(1000 - 181, t.Pnl);
    }

    [Fact]
    public async Task OpenPositionAtRunEnd_ReportedWithoutExit()
    {
        WriteEvents(
            """{"ts":"2026-03-01T15:00:00+00:00","sq":2,"_t":"ord.fill","src":"engine","d":{"orderId":-1,"assetName":"NFLX","side":"buy","price":9000,"quantity":5,"commission":45}}""");

        var trades = await CreateReader().Read(_runDir, Ct);

        var t = Assert.Single(trades);
        Assert.Null(t.ExitTime);
        Assert.Null(t.ExitPrice);
        Assert.Null(t.Pnl);
    }

    [Fact]
    public async Task MissingEventsFile_ReturnsEmpty()
    {
        var trades = await CreateReader().Read(_runDir, Ct);
        Assert.Empty(trades);
    }

    [Fact]
    public async Task CancelledEntry_PendingSlTp_DoesNotLeakIntoNextTrade()
    {
        // Group places SL/TP but the entry is cancelled (never fills). A later raw trade on the
        // same asset must not inherit the orphaned SL/TP.
        WriteEvents(
            """{"ts":"2026-01-23T14:45:00+00:00","sq":2,"_t":"grp","src":"trade-registry","d":{"groupId":1,"assetName":"NFLX","transition":"slPlaced","orderId":-2,"price":8600,"quantity":54.5}}""",
            """{"ts":"2026-01-23T14:45:00+00:00","sq":3,"_t":"grp","src":"trade-registry","d":{"groupId":1,"assetName":"NFLX","transition":"tpPlaced","orderId":-3,"price":8400,"quantity":54.5}}""",
            """{"ts":"2026-01-23T16:00:00+00:00","sq":4,"_t":"grp","src":"trade-registry","d":{"groupId":1,"assetName":"NFLX","transition":"entryCancelled","orderId":-1,"price":8512,"quantity":54.5}}""",
            """{"ts":"2026-01-24T15:00:00+00:00","sq":5,"_t":"ord.fill","src":"engine","d":{"orderId":8,"assetName":"NFLX","side":"buy","price":9000,"quantity":10,"commission":0}}""",
            """{"ts":"2026-01-24T16:00:00+00:00","sq":6,"_t":"ord.fill","src":"engine","d":{"orderId":9,"assetName":"NFLX","side":"sell","price":9050,"quantity":10,"commission":0}}""");

        var trades = await CreateReader().Read(_runDir, Ct);

        var t = Assert.Single(trades);
        Assert.Null(t.StopLossPrice);
        Assert.Null(t.TakeProfitPrice);
    }

    [Fact]
    public async Task SlTpFromEarlierGroup_DoesNotLeakIntoNextTrade()
    {
        // Trade 1 has SL/TP; trade 2 is a raw pair with no group — must have null SL/TP.
        WriteEvents(
            """{"ts":"2026-01-23T14:55:00+00:00","sq":2,"_t":"ord.fill","src":"engine","d":{"orderId":-1,"assetName":"NFLX","side":"sell","price":8512,"quantity":54.5,"commission":0}}""",
            """{"ts":"2026-01-23T14:55:00+00:00","sq":3,"_t":"grp","src":"trade-registry","d":{"groupId":1,"assetName":"NFLX","transition":"slPlaced","orderId":-2,"price":8600,"quantity":54.5}}""",
            """{"ts":"2026-01-23T16:10:00+00:00","sq":4,"_t":"ord.fill","src":"engine","d":{"orderId":-3,"assetName":"NFLX","side":"buy","price":8400,"quantity":54.5,"commission":0}}""",
            """{"ts":"2026-01-24T15:00:00+00:00","sq":5,"_t":"ord.fill","src":"engine","d":{"orderId":8,"assetName":"NFLX","side":"buy","price":9000,"quantity":10,"commission":0}}""",
            """{"ts":"2026-01-24T16:00:00+00:00","sq":6,"_t":"ord.fill","src":"engine","d":{"orderId":9,"assetName":"NFLX","side":"sell","price":9050,"quantity":10,"commission":0}}""");

        var trades = await CreateReader().Read(_runDir, Ct);

        Assert.Equal(2, trades.Count);
        Assert.Equal(8600, trades[0].StopLossPrice);
        Assert.Null(trades[1].StopLossPrice);
        Assert.Null(trades[1].TakeProfitPrice);
    }
}
