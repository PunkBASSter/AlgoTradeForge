using System.Globalization;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.HistoryLoader.Application.Aggregation;
using AlgoTradeForge.HistoryLoader.Infrastructure.Storage;
using AlgoTradeForge.Infrastructure.IO;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Aggregation;

/// <summary>
/// End-to-end EqID (Dollar Imbalance) pipeline coverage. Mirrors AggregationPipeline_EqITests.
/// Two source paths:
/// <list type="bullet">
///   <item>Tick: per-trade <c>signed_qty × Close</c> contribution; manifest tag
///         <c>tick_signed_dollar</c>.</item>
///   <item>Time-bar: <c>candle-ext.taker_buy_quote_vol</c> pre-multiplied by joiner;
///         manifest tag <c>m1_taker_buy_quote_proxy</c>.</item>
/// </list>
/// Pipeline-level invariants asserted: sidecar partition file written, manifest's
/// <c>Sidecar</c> field points at the live sidecar entry, sidecar columns named per
/// <see cref="EqIDAccumulator.Schema"/>.
/// </summary>
public sealed class AggregationPipeline_EqIDTests : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), $"AggregationPipeline_EqIDTests_{Guid.NewGuid():N}");

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

    private AggregationJob EqIDJob(string asset, DataFeedKind sourceKind, string sourceFeedId,
        long thresholdScaled, decimal thresholdAbs)
    {
        var scale = new ScaleContext(tickSize: 0.01m, quantityStepSize: 0.0001m);
        return new AggregationJob(
            JobId: "job-eqid-test",
            Source: new DataFeedDescriptor(_tempDir, "binance", asset, sourceFeedId, sourceKind),
            AssetDir: AssetDir(asset),
            OutcomeFeedId: $"EqID_{sourceFeedId}_{thresholdAbs}",
            TypeCode: "EqID",
            ThresholdAbsolute: thresholdAbs,
            ThresholdScaled: thresholdScaled,
            ThresholdUnit: "quote_asset",
            ThresholdInputMode: "absolute",
            ThresholdConvenienceInput: null,
            SourceScale: scale,
            AccumulatorScale: scale,
            MaxPartitionSizeMB: 1,
            ToolVersion: "test-1.0");
    }

    private static AggregationPipeline NewPipeline() =>
        new(new PartitionedSourceReader(),
            new FeedSchemaManager(new LocalFileStorage()),
            new OverwritePathWriter(),
            TimeProvider.System);

    // -----------------------------------------------------------------------
    // Tick-source EqID
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Run_TickEqID_AllBuy_PositiveDollarImbalance_ManifestTaggedTickSignedDollar()
    {
        // tickSize=0.01, qty step=0.0001 → QuantityScale=10000, ScaleFactor=100,
        // dollarTickPerDollar = 1,000,000.
        // Each tick: qty=400 long (= 0.04 base), price=5,000,000 long (= $50,000).
        //   contribution = 400 × 5,000,000 = 2e9 dollar-tick (= $2000 per tick).
        // 3 ticks → cum +6e9. Threshold = 5e9 → emit at 3rd tick (overshoot 20%).
        const string asset = "BTCUSDT_ticks";
        WriteTicks(asset, "2024-03-15",
            (Ts(2024, 3, 15, 12, 0, 0), 5_000_000, 400, 0, 1000),
            (Ts(2024, 3, 15, 12, 0, 1), 5_000_010, 400, 0, 1001),
            (Ts(2024, 3, 15, 12, 0, 2), 5_000_020, 400, 0, 1002));

        var result = await NewPipeline().Run(
            EqIDJob(asset, DataFeedKind.Tick, sourceFeedId: "ticks",
                thresholdScaled: 5_000_000_000L, thresholdAbs: 5000m),
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, result.BarCount);
        Assert.Equal("EqID_ticks_5000.flow", result.SidecarFeedId);

        var manifest = (await new FeedSchemaManager(new LocalFileStorage()).Load(AssetDir(asset), TestContext.Current.CancellationToken))!;
        var parent = manifest.Feeds["EqID_ticks_5000"];
        Assert.Equal("EqID", parent.Type!.Code);
        Assert.Equal("tick_signed_dollar", parent.Fidelity!.ImbalanceReconstructionMethod);
        Assert.Equal("EqID_ticks_5000.flow", parent.Sidecar);

        var sidecarDef = manifest.Feeds["EqID_ticks_5000.flow"];
        Assert.Equal(
            new[] { "signed_dollar_imbalance", "buy_dollar", "sell_dollar", "realized_threshold" },
            sidecarDef.Columns);

        // Sidecar partition: 100% buy → positive signed_dollar_imbalance.
        var sidecarPath = Path.Combine(AssetDir(asset), "aggregated", "EqID_ticks_5000.flow", "2024-03.csv");
        Assert.True(File.Exists(sidecarPath));
        var lines = File.ReadAllLines(sidecarPath);
        Assert.Equal("ts,signed_dollar_imbalance,buy_dollar,sell_dollar,realized_threshold", lines[0]);
        var parts = lines[1].Split(',');
        var signed = double.Parse(parts[1], CultureInfo.InvariantCulture);
        var buy = double.Parse(parts[2], CultureInfo.InvariantCulture);
        var sell = double.Parse(parts[3], CultureInfo.InvariantCulture);
        Assert.True(signed > 0d);
        Assert.Equal(0d, sell);
        // 3 trades × $2000 each = $6000 buy cumulative.
        Assert.Equal(6000d, buy, 1);
        Assert.Equal(6000d, signed, 1);
    }

    [Fact]
    public async Task Run_TickEqID_AllSell_NegativeDollarImbalance()
    {
        const string asset = "BTCUSDT_ticks_sell";
        WriteTicks(asset, "2024-03-15",
            (Ts(2024, 3, 15, 12, 0, 0), 5_000_000, 400, 1, 1000),
            (Ts(2024, 3, 15, 12, 0, 1), 5_000_010, 400, 1, 1001),
            (Ts(2024, 3, 15, 12, 0, 2), 5_000_020, 400, 1, 1002));

        var result = await NewPipeline().Run(
            EqIDJob(asset, DataFeedKind.Tick, sourceFeedId: "ticks",
                thresholdScaled: 5_000_000_000L, thresholdAbs: 5000m),
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, result.BarCount);
        var sidecarPath = Path.Combine(AssetDir(asset), "aggregated", "EqID_ticks_5000.flow", "2024-03.csv");
        var parts = File.ReadAllLines(sidecarPath)[1].Split(',');
        Assert.True(double.Parse(parts[1], CultureInfo.InvariantCulture) < 0d);
        Assert.Equal(0d, double.Parse(parts[2], CultureInfo.InvariantCulture));
    }

    // -----------------------------------------------------------------------
    // Time-bar EqID via taker_buy_quote_vol proxy
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Run_TimeBarEqID_TakerBuyQuoteProxy_ManifestTaggedQuoteProxy()
    {
        // Per record: candle-ext exposes taker_buy_quote_vol = $50 (50 dollars), and
        // quote_vol = $100. Joiner pre-scales:
        //   buyDollarTick = 50 × QuantityScale × ScaleFactor = 50 × 10000 × 100 = 5e7
        //   sellDollarTick = (100 - 50) × 1e6 = 5e7
        //   signed = 0
        // To test positive signed: bar 1 has all-buy ($100 buy, $0 sell).
        const string asset = "BTCUSDT_perp";
        WriteCandles(asset, "2024-01", "1m",
            (Ts(2024, 1, 1, 0), 100, 110, 95, 105, 400),
            (Ts(2024, 1, 1, 1), 105, 115, 100, 110, 400),
            (Ts(2024, 1, 1, 2), 110, 120, 105, 118, 400));
        // 100% taker-buy: every record's taker_buy_quote_vol == quote_vol.
        // Per-record: buy = $100 × 1e6 = 1e8 dollar-tick; sell = 0; signed = +1e8.
        // 3 records → cum +3e8 (= $300 in raw dollar terms).
        WriteCandleExt(asset, "2024-01", "1m",
            (Ts(2024, 1, 1, 0), 100.0, 50L, 0.04, 100.0, 50L),
            (Ts(2024, 1, 1, 1), 100.0, 50L, 0.04, 100.0, 50L),
            (Ts(2024, 1, 1, 2), 100.0, 50L, 0.04, 100.0, 50L));

        // Threshold: $250 → 250 × QuantityScale × ScaleFactor = 250 × 1e6 = 2.5e8.
        var result = await NewPipeline().Run(
            EqIDJob(asset, DataFeedKind.TimeBar, sourceFeedId: "1m",
                thresholdScaled: 250_000_000L, thresholdAbs: 250m),
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, result.BarCount);

        var manifest = (await new FeedSchemaManager(new LocalFileStorage()).Load(AssetDir(asset), TestContext.Current.CancellationToken))!;
        var parent = manifest.Feeds["EqID_1m_250"];
        Assert.Equal("m1_taker_buy_quote_proxy", parent.Fidelity!.ImbalanceReconstructionMethod);

        var sidecarPath = Path.Combine(AssetDir(asset), "aggregated", "EqID_1m_250.flow", "2024-01.csv");
        var parts = File.ReadAllLines(sidecarPath)[1].Split(',');
        var signed = double.Parse(parts[1], CultureInfo.InvariantCulture);
        var buy = double.Parse(parts[2], CultureInfo.InvariantCulture);
        var sell = double.Parse(parts[3], CultureInfo.InvariantCulture);
        Assert.True(signed > 0d);
        Assert.Equal(300d, buy, 1);    // $300 buy cumulative
        Assert.Equal(0d, sell, 1);
        Assert.Equal(300d, signed, 1);
    }
}
