using System.Globalization;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.HistoryLoader.Application.Aggregation;
using AlgoTradeForge.HistoryLoader.Infrastructure.Storage;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Aggregation;

/// <summary>
/// Phase 2b — end-to-end EqIV pipeline coverage. Two source paths (TRD §6.3):
/// <list type="bullet">
///   <item>P2b-7: tick-source EqIV, manifest tag <c>tick_signed</c>.</item>
///   <item>P2b-8: time-bar EqIV via <c>candle-ext</c> proxy, manifest tag
///         <c>m1_taker_buy_proxy</c>; sign convention pinned with 100%-taker-buy fixture.</item>
/// </list>
/// Beyond the accumulator unit tests, this fixture covers the pipeline-level invariants:
/// sidecar partition file written, sidecar manifest entry registered, parent's
/// <c>Sidecar</c> field points at the live sidecar.
/// </summary>
public sealed class AggregationPipeline_EqITests : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), $"AggregationPipeline_EqITests_{Guid.NewGuid():N}");

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

    private void WriteCandleExt(string asset, string month, string interval,
        params (long ts, double quoteVol, long tradeCount, double takerBuyVol, double takerBuyQuoteVol)[] rows)
    {
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

    private AggregationJob EqIJob(string asset, DataFeedKind sourceKind, string sourceFeedId,
        long thresholdScaled, decimal thresholdAbs)
    {
        var scale = new ScaleContext(tickSize: 0.01m, quantityStepSize: 0.0001m);
        return new AggregationJob(
            JobId: "job-eqi-test",
            Source: new DataFeedDescriptor(_tempDir, "binance", asset, sourceFeedId, sourceKind),
            AssetDir: AssetDir(asset),
            OutcomeFeedId: $"EqIV_{sourceFeedId}_{thresholdAbs}",
            TypeCode: "EqIV",
            ThresholdAbsolute: thresholdAbs,
            ThresholdScaled: thresholdScaled,
            ThresholdUnit: "base_asset",
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
    // P2b-8 — time-bar EqIV taker-buy proxy
    // -----------------------------------------------------------------------

    [Fact]
    public void Run_TimeBarEqIV_AllTakerBuy_PositiveSignedImbalance_ManifestTagged()
    {
        const string asset = "BTCUSDT_perp";

        // 100%-taker-buy fixture: every candle has taker_buy_vol == vol.
        // Per-record: BuyVolumeLong = ToLong(taker_buy * QuantityScale = vol_long),
        //             SellVolumeLong = vol_long - BuyVolumeLong = 0.
        // signed_acc = +vol cumulatively.
        // QuantityScale = 1/0.0001 = 10000, so taker_buy_double 0.04 BTC → 400 long.
        WriteCandles(asset, "2024-01", "1m",
            (Ts(2024, 1, 1, 0), 100, 110, 95, 105, 400),
            (Ts(2024, 1, 1, 1), 105, 115, 100, 110, 400),
            (Ts(2024, 1, 1, 2), 110, 120, 105, 118, 400));
        WriteCandleExt(asset, "2024-01", "1m",
            (Ts(2024, 1, 1, 0), 0.0, 0L, 0.04, 0.0),
            (Ts(2024, 1, 1, 1), 0.0, 0L, 0.04, 0.0),
            (Ts(2024, 1, 1, 2), 0.0, 0L, 0.04, 0.0));

        var result = NewPipeline().Run(
            EqIJob(asset, DataFeedKind.TimeBar, sourceFeedId: "1m",
                thresholdScaled: 1000, thresholdAbs: 1000m),
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, result.BarCount);
        Assert.Equal("EqIV_1m_1000.flow", result.SidecarFeedId);

        var manifest = new FeedSchemaManager().Load(AssetDir(asset))!;

        var parent = manifest.Feeds["EqIV_1m_1000"];
        Assert.Equal("EqIV", parent.Type!.Code);
        Assert.Equal("EqIV_1m_1000.flow", parent.Sidecar);
        Assert.Equal("m1_taker_buy_proxy", parent.Fidelity!.ImbalanceReconstructionMethod);

        var sidecarDef = manifest.Feeds["EqIV_1m_1000.flow"];
        Assert.Equal("Side", sidecarDef.Kind);
        Assert.True(sidecarDef.NullableColumns ?? false);
        Assert.Equal(
            new[] { "signed_imbalance", "buy_volume", "sell_volume", "realized_threshold" },
            sidecarDef.Columns);

        // Sidecar partition file written.
        var sidecarPartition = Path.Combine(AssetDir(asset), "aggregated", "EqIV_1m_1000.flow", "2024-01.csv");
        Assert.True(File.Exists(sidecarPartition));

        // Verify sidecar row content: 100% taker-buy → signed_imbalance == buy_volume.
        var lines = File.ReadAllLines(sidecarPartition);
        Assert.Equal("ts,signed_imbalance,buy_volume,sell_volume,realized_threshold", lines[0]);
        var dataParts = lines[1].Split(',');
        var signed = double.Parse(dataParts[1], CultureInfo.InvariantCulture);
        var buy    = double.Parse(dataParts[2], CultureInfo.InvariantCulture);
        var sell   = double.Parse(dataParts[3], CultureInfo.InvariantCulture);
        var realized = double.Parse(dataParts[4], CultureInfo.InvariantCulture);
        Assert.True(signed > 0d);
        Assert.True(buy > 0d);
        Assert.Equal(0d, sell);
        Assert.Equal(buy, signed, 6);
        Assert.Equal(Math.Abs(signed), realized, 6);
    }

    [Fact]
    public void Run_TimeBarEqIV_TakerBuyProxy_FormulaMatchesTrd()
    {
        // TRD §6.3 formula: signed_imbalance = 2 * taker_buy - vol (per record, summed).
        // Test fixture: source vols = 400 each, taker_buy = 0.03 (300 long) — so per-record
        // signed contribution = 2*300 - 400 = +200 long. Three records → signed_acc = +600 long
        // → emit at threshold 600.
        const string asset = "BTCUSDT_perp_proxy";

        WriteCandles(asset, "2024-01", "1m",
            (Ts(2024, 1, 1, 0), 100, 110, 95, 105, 400),
            (Ts(2024, 1, 1, 1), 105, 115, 100, 110, 400),
            (Ts(2024, 1, 1, 2), 110, 120, 105, 118, 400));
        WriteCandleExt(asset, "2024-01", "1m",
            (Ts(2024, 1, 1, 0), 0.0, 0L, 0.03, 0.0),
            (Ts(2024, 1, 1, 1), 0.0, 0L, 0.03, 0.0),
            (Ts(2024, 1, 1, 2), 0.0, 0L, 0.03, 0.0));

        var result = NewPipeline().Run(
            EqIJob(asset, DataFeedKind.TimeBar, sourceFeedId: "1m",
                thresholdScaled: 600, thresholdAbs: 600m),
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, result.BarCount);

        // Read sidecar row to confirm formula.
        var sidecarPath = Path.Combine(AssetDir(asset), "aggregated", "EqIV_1m_600.flow", "2024-01.csv");
        var lines = File.ReadAllLines(sidecarPath);
        var parts = lines[1].Split(',');
        var buyDouble    = double.Parse(parts[2], CultureInfo.InvariantCulture);
        var sellDouble   = double.Parse(parts[3], CultureInfo.InvariantCulture);
        var signedDouble = double.Parse(parts[1], CultureInfo.InvariantCulture);

        // Buy = 0.09 BTC (3 × 0.03), Sell = 0.03 BTC (vol_total 0.12 - 0.09)
        Assert.Equal(0.09, buyDouble, 6);
        Assert.Equal(0.03, sellDouble, 6);
        Assert.Equal(0.06, signedDouble, 6);
        // Verify TRD formula on raw doubles: signed = 2*taker_buy - total_vol
        var totalVolDouble = 3 * 400 / 10000.0;             // 0.12
        var totalTakerBuyDouble = 3 * 0.03;                  // 0.09
        Assert.Equal(2 * totalTakerBuyDouble - totalVolDouble, signedDouble, 6);
    }

    [Fact]
    public void Run_TimeBarEqIV_NoCandleExt_NoBarsEmitted()
    {
        // Partial coverage (TRD §6.2): time-bar EqIV with no candle-ext on disk yields zero
        // bars (every source record is dropped at the join). Manifest still written so the
        // run is not silently lost.
        const string asset = "BTCUSDT_perp_no_ext";

        WriteCandles(asset, "2024-01", "1m",
            (Ts(2024, 1, 1, 0), 100, 110, 95, 105, 400));
        // No candle-ext written — directory missing entirely.

        var result = NewPipeline().Run(
            EqIJob(asset, DataFeedKind.TimeBar, sourceFeedId: "1m",
                thresholdScaled: 100, thresholdAbs: 100m),
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(0, result.BarCount);

        // Manifest still includes both entries (parent + empty sidecar) — atomic write.
        var manifest = new FeedSchemaManager().Load(AssetDir(asset))!;
        Assert.Contains("EqIV_1m_100", manifest.Feeds);
        Assert.Contains("EqIV_1m_100.flow", manifest.Feeds);
    }

    // -----------------------------------------------------------------------
    // P2b-7 — tick-source EqIV
    // -----------------------------------------------------------------------

    [Fact]
    public void Run_TickEqIV_AllBuy_ManifestTaggedTickSigned()
    {
        // 100%-buy tick fixture. is_buyer_maker=0 → +qty. Threshold 1000.
        const string asset = "BTCUSDT_ticks";

        WriteTicks(asset, "2024-03-15",
            (Ts(2024, 3, 15, 12, 0, 0), 5_000_000, 400, 0, 1000),
            (Ts(2024, 3, 15, 12, 0, 1), 5_000_010, 400, 0, 1001),
            (Ts(2024, 3, 15, 12, 0, 2), 5_000_020, 400, 0, 1002));

        var result = NewPipeline().Run(
            EqIJob(asset, DataFeedKind.Tick, sourceFeedId: "ticks",
                thresholdScaled: 1000, thresholdAbs: 1000m),
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, result.BarCount);
        Assert.Equal("EqIV_ticks_1000.flow", result.SidecarFeedId);

        var manifest = new FeedSchemaManager().Load(AssetDir(asset))!;
        var parent = manifest.Feeds["EqIV_ticks_1000"];
        Assert.Equal("tick_signed", parent.Fidelity!.ImbalanceReconstructionMethod);
        Assert.Equal("EqIV_ticks_1000.flow", parent.Sidecar);

        // Sidecar row sanity: all-buy → positive signed_imbalance.
        var sidecarPath = Path.Combine(AssetDir(asset), "aggregated", "EqIV_ticks_1000.flow", "2024-03.csv");
        Assert.True(File.Exists(sidecarPath));
        var lines = File.ReadAllLines(sidecarPath);
        var parts = lines[1].Split(',');
        Assert.True(double.Parse(parts[1], CultureInfo.InvariantCulture) > 0d);   // signed > 0
        Assert.Equal(0d, double.Parse(parts[3], CultureInfo.InvariantCulture));   // sell == 0
    }

    [Fact]
    public void Run_TickEqIV_AllSell_NegativeSignedImbalance()
    {
        const string asset = "BTCUSDT_ticks_sell";
        WriteTicks(asset, "2024-03-15",
            (Ts(2024, 3, 15, 12, 0, 0), 5_000_000, 400, 1, 1000),
            (Ts(2024, 3, 15, 12, 0, 1), 5_000_010, 400, 1, 1001),
            (Ts(2024, 3, 15, 12, 0, 2), 5_000_020, 400, 1, 1002));

        var result = NewPipeline().Run(
            EqIJob(asset, DataFeedKind.Tick, sourceFeedId: "ticks",
                thresholdScaled: 1000, thresholdAbs: 1000m),
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, result.BarCount);

        var sidecarPath = Path.Combine(AssetDir(asset), "aggregated", "EqIV_ticks_1000.flow", "2024-03.csv");
        var lines = File.ReadAllLines(sidecarPath);
        var parts = lines[1].Split(',');
        var signed = double.Parse(parts[1], CultureInfo.InvariantCulture);
        var buy = double.Parse(parts[2], CultureInfo.InvariantCulture);
        Assert.True(signed < 0d);
        Assert.Equal(0d, buy);
    }
}
