using System.Globalization;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.HistoryLoader.Application.Aggregation;
using AlgoTradeForge.HistoryLoader.Infrastructure.Storage;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Aggregation;

/// <summary>
/// End-to-end EqIT (Tick-count Imbalance) pipeline coverage. Two source paths:
/// <list type="bullet">
///   <item>Tick: ±1 per record (sign of <c>BuyVolumeLong − SellVolumeLong</c>); manifest
///         tag <c>tick_signed_count</c>.</item>
///   <item>Time-bar: <c>candle-ext.taker_buy_trade_count</c> (a kline-derived proxy)
///         populates <c>BuyTradeCountLong</c> / <c>SellTradeCountLong</c>; manifest tag
///         <c>m1_taker_buy_count_proxy</c>.</item>
/// </list>
/// Also pins the failure mode for time-bar EqIT against a candle-ext partition that
/// pre-dates the schema bump (missing <c>taker_buy_trade_count</c> column).
/// </summary>
public sealed class AggregationPipeline_EqITTests : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), $"AggregationPipeline_EqITTests_{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private string AssetDir(string asset) => Path.Combine(_tempDir, "binance", asset);

    private static long Ts(int year, int month, int day, int hour = 0, int minute = 0, int second = 0) =>
        new DateTimeOffset(year, month, day, hour, minute, second, TimeSpan.Zero).ToUnixTimeMilliseconds();

    private void WriteCandles(string asset, string month, string interval,
        params (long ts, long o, long h, long l, long c, long v)[] rows)
    {
        var dir = Path.Combine(AssetDir(asset), "candles");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{month}_{interval}.csv");
        using var sw = new StreamWriter(path);
        sw.WriteLine("ts,o,h,l,c,vol");
        foreach (var r in rows)
            sw.WriteLine($"{r.ts},{r.o},{r.h},{r.l},{r.c},{r.v}");
    }

    private void WriteCandleExtNew(string asset, string month, string interval,
        params (long ts, double quoteVol, long tradeCount, double takerBuyVol,
                double takerBuyQuoteVol, long takerBuyTradeCount)[] rows)
    {
        var dir = Path.Combine(AssetDir(asset), "candle-ext");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{month}_{interval}.csv");
        using var sw = new StreamWriter(path);
        sw.WriteLine("ts,quote_vol,trade_count,taker_buy_vol,taker_buy_quote_vol,taker_buy_trade_count");
        foreach (var r in rows)
            sw.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "{0},{1},{2},{3},{4},{5}",
                r.ts, r.quoteVol, r.tradeCount, r.takerBuyVol, r.takerBuyQuoteVol, r.takerBuyTradeCount));
    }

    private void WriteCandleExtOldSchema(string asset, string month, string interval,
        params (long ts, double quoteVol, long tradeCount, double takerBuyVol, double takerBuyQuoteVol)[] rows)
    {
        // Old (pre-Phase D) schema: no taker_buy_trade_count column. Used to assert
        // EqIT-on-TimeBar fails loud against legacy partitions, while EqI continues to work.
        var dir = Path.Combine(AssetDir(asset), "candle-ext");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{month}_{interval}.csv");
        using var sw = new StreamWriter(path);
        sw.WriteLine("ts,quote_vol,trade_count,taker_buy_vol,taker_buy_quote_vol");
        foreach (var r in rows)
            sw.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "{0},{1},{2},{3},{4}",
                r.ts, r.quoteVol, r.tradeCount, r.takerBuyVol, r.takerBuyQuoteVol));
    }

    private void WriteTicks(string asset, string day,
        params (long ts, long price, long qty, int isBuyerMaker, long aggId)[] rows)
    {
        var dir = Path.Combine(AssetDir(asset), "ticks");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{day}.csv");
        using var sw = new StreamWriter(path);
        sw.WriteLine("ts,price,qty,is_buyer_maker,agg_id");
        foreach (var r in rows)
            sw.WriteLine($"{r.ts},{r.price},{r.qty},{r.isBuyerMaker},{r.aggId}");
    }

    private AggregationJob EqITJob(string asset, DataFeedKind sourceKind, string sourceFeedId,
        long thresholdScaled, decimal thresholdAbs)
    {
        var scale = new ScaleContext(tickSize: 0.01m, quantityStepSize: 0.0001m);
        return new AggregationJob(
            JobId: "job-eqit-test",
            Source: new DataFeedDescriptor(_tempDir, "binance", asset, sourceFeedId, sourceKind),
            AssetDir: AssetDir(asset),
            OutcomeFeedId: $"EqIT_{sourceFeedId}_{thresholdAbs}",
            TypeCode: "EqIT",
            ThresholdAbsolute: thresholdAbs,
            ThresholdScaled: thresholdScaled,
            ThresholdUnit: "trades",
            ThresholdInputMode: "absolute",
            ThresholdConvenienceInput: null,
            SourceScale: scale,
            AccumulatorScale: scale,
            MaxPartitionSizeMB: 1,
            ToolVersion: "test-1.0");
    }

    private static AggregationPipeline NewPipeline() =>
        new(new PartitionedSourceReader(),
            new FeedSchemaManager(),
            new OverwritePathWriter(),
            TimeProvider.System);

    // -----------------------------------------------------------------------
    // Tick-source EqIT
    // -----------------------------------------------------------------------

    [Fact]
    public void Run_TickEqIT_AllBuy_PositiveCountImbalance_ManifestTaggedTickSignedCount()
    {
        const string asset = "BTCUSDT_ticks";
        // 3 buy ticks → signed_count = +3 → emit at threshold 3.
        WriteTicks(asset, "2024-03-15",
            (Ts(2024, 3, 15, 12, 0, 0), 5_000_000, 400, 0, 1000),
            (Ts(2024, 3, 15, 12, 0, 1), 5_000_010, 400, 0, 1001),
            (Ts(2024, 3, 15, 12, 0, 2), 5_000_020, 400, 0, 1002));

        var result = NewPipeline().Run(
            EqITJob(asset, DataFeedKind.Tick, sourceFeedId: "ticks",
                thresholdScaled: 3, thresholdAbs: 3m),
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, result.BarCount);
        Assert.Equal("EqIT_ticks_3.flow", result.SidecarFeedId);

        var manifest = new FeedSchemaManager().Load(AssetDir(asset))!;
        var parent = manifest.Feeds["EqIT_ticks_3"];
        Assert.Equal("EqIT", parent.Type!.Code);
        Assert.Equal("tick_signed_count", parent.Fidelity!.ImbalanceReconstructionMethod);

        var sidecarDef = manifest.Feeds["EqIT_ticks_3.flow"];
        Assert.Equal(
            new[] { "signed_count_imbalance", "buy_trade_count", "sell_trade_count", "realized_threshold" },
            sidecarDef.Columns);

        // Sidecar partition: 3 buys, 0 sells → signed_count = +3.
        var sidecarPath = Path.Combine(AssetDir(asset), "aggregated", "EqIT_ticks_3.flow", "2024-03.csv");
        var lines = File.ReadAllLines(sidecarPath);
        Assert.Equal("ts,signed_count_imbalance,buy_trade_count,sell_trade_count,realized_threshold", lines[0]);
        var parts = lines[1].Split(',');
        Assert.Equal(3d, double.Parse(parts[1], CultureInfo.InvariantCulture));   // signed
        Assert.Equal(3d, double.Parse(parts[2], CultureInfo.InvariantCulture));   // buy_trade_count
        Assert.Equal(0d, double.Parse(parts[3], CultureInfo.InvariantCulture));   // sell_trade_count
    }

    // -----------------------------------------------------------------------
    // Time-bar EqIT via taker_buy_trade_count proxy
    // -----------------------------------------------------------------------

    [Fact]
    public void Run_TimeBarEqIT_TakerBuyCountProxy_ManifestTaggedCountProxy()
    {
        const string asset = "BTCUSDT_perp";
        WriteCandles(asset, "2024-01", "1m",
            (Ts(2024, 1, 1, 0), 100, 110, 95, 105, 400),
            (Ts(2024, 1, 1, 1), 105, 115, 100, 110, 400),
            (Ts(2024, 1, 1, 2), 110, 120, 105, 118, 400));
        // Per-bar trade_count = 30, taker_buy_trade_count = 25 → buy=25, sell=5, signed=+20.
        // 3 bars → cum signed = +60.
        WriteCandleExtNew(asset, "2024-01", "1m",
            (Ts(2024, 1, 1, 0), 100.0, 30L, 0.04, 100.0, 25L),
            (Ts(2024, 1, 1, 1), 100.0, 30L, 0.04, 100.0, 25L),
            (Ts(2024, 1, 1, 2), 100.0, 30L, 0.04, 100.0, 25L));

        var result = NewPipeline().Run(
            EqITJob(asset, DataFeedKind.TimeBar, sourceFeedId: "1m",
                thresholdScaled: 50, thresholdAbs: 50m),
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, result.BarCount);

        var manifest = new FeedSchemaManager().Load(AssetDir(asset))!;
        var parent = manifest.Feeds["EqIT_1m_50"];
        Assert.Equal("m1_taker_buy_count_proxy", parent.Fidelity!.ImbalanceReconstructionMethod);

        var sidecarPath = Path.Combine(AssetDir(asset), "aggregated", "EqIT_1m_50.flow", "2024-01.csv");
        var parts = File.ReadAllLines(sidecarPath)[1].Split(',');
        Assert.Equal(60d, double.Parse(parts[1], CultureInfo.InvariantCulture));   // signed = +60
        Assert.Equal(75d, double.Parse(parts[2], CultureInfo.InvariantCulture));   // buy_count = 25×3
        Assert.Equal(15d, double.Parse(parts[3], CultureInfo.InvariantCulture));   // sell_count = 5×3
    }

    // -----------------------------------------------------------------------
    // Backfill: legacy candle-ext partition lacking taker_buy_trade_count
    // -----------------------------------------------------------------------

    [Fact]
    public void Run_TimeBarEqIT_LegacyCandleExtMissingNewColumn_ThrowsRemediationMessage()
    {
        const string asset = "BTCUSDT_perp_legacy";
        WriteCandles(asset, "2024-01", "1m",
            (Ts(2024, 1, 1, 0), 100, 110, 95, 105, 400));
        WriteCandleExtOldSchema(asset, "2024-01", "1m",
            (Ts(2024, 1, 1, 0), 100.0, 30L, 0.04, 100.0));    // no taker_buy_trade_count column

        // Joiner throws when it can't resolve the column. Pipeline propagates the exception
        // (the eligibility layer is meant to pre-flight this; the runtime guard is defense-in-depth).
        var ex = Assert.Throws<InvalidOperationException>(() => NewPipeline().Run(
            EqITJob(asset, DataFeedKind.TimeBar, sourceFeedId: "1m",
                thresholdScaled: 50, thresholdAbs: 50m),
            ct: TestContext.Current.CancellationToken));

        Assert.Contains("taker_buy_trade_count", ex.Message);
        Assert.Contains("Re-fetch candle-ext", ex.Message);
    }
}
