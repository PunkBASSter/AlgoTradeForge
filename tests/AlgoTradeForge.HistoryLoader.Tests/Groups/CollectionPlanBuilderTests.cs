using System.Text.Json;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.HistoryLoader.Application.Groups;
using AlgoTradeForge.HistoryLoader.Application.Index;
using AlgoTradeForge.HistoryLoader.Domain;
using AlgoTradeForge.HistoryLoader.Domain.Symbology;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Groups;

public sealed class CollectionPlanBuilderTests
{
    private static readonly VenueInstrument BtcVenue = new("BTCUSDT", AssetTypes.Perpetual, "BTCUSDT_perp");

    private static DesiredTuple T(
        string feed, string interval,
        bool isDerived = false,
        string start = "2020-01") =>
        new("binance", "BTC/USDT", BtcVenue, feed, interval, "eager", "csv", start, isDerived, []);

    private static DesiredState State(params DesiredTuple[] tuples) =>
        new(tuples, [], []);

    private static IReadOnlyList<InstrumentMetaRow> Meta(int priceDecimals) =>
        [new("binance", "BTCUSDT_perp", priceDecimals, 4, "0.01", "2024-01-01T00:00:00Z")];

    private static IReadOnlyDictionary<(string, string), int> Recorded(int digits) =>
        new Dictionary<(string, string), int> { [("binance", "BTCUSDT_perp")] = digits };

    private static IReadOnlyDictionary<(string, string), int> NoRecorded() =>
        new Dictionary<(string, string), int>();

    [Fact]
    public void Build_GroupsTuplesPerVenue_AndSkipsDerivedAndUnsupported()
    {
        var nullVenueTuple = new DesiredTuple("binance", "ETH/USDT", null, "candles", "1h", "eager", "csv", "2020-01", false, []);
        var state = State(
            T("candles", "1h"),
            T("mark-price", "1h"),
            T("EqV_1m_1k", "", isDerived: true),  // skipped: derived
            nullVenueTuple);                        // skipped: Venue==null

        var plan = CollectionPlanBuilder.Build(state, [], Meta(2), NoRecorded());

        Assert.Single(plan.Assets);
        var asset = plan.Assets[0];
        Assert.Equal("BTC/USDT", asset.Canonical);
        Assert.Equal(2, asset.Feeds.Count);
        Assert.Contains(asset.Feeds, f => f.FeedName == "candles");
        Assert.Contains(asset.Feeds, f => f.FeedName == "mark-price");
        // Feeds ordered: "candles" < "mark-price" (Ordinal)
        Assert.Equal("candles", asset.Feeds[0].FeedName);
        Assert.Equal("mark-price", asset.Feeds[1].FeedName);
        Assert.Empty(plan.Blocked);
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void Build_RecordedScaleWins_AndDivergenceWarns()
    {
        var state = State(T("candles", "1h"));

        var plan = CollectionPlanBuilder.Build(state, [], Meta(1), Recorded(2));

        Assert.Single(plan.Assets);
        Assert.Equal(2, plan.Assets[0].DecimalDigits);
        Assert.Single(plan.Warnings);
        Assert.Contains("disk scale 2", plan.Warnings[0].Message);
        Assert.Contains("exchangeInfo 1", plan.Warnings[0].Message);
    }

    [Fact]
    public void Build_NoRecordedNoMeta_BlocksAsset()
    {
        var state = State(T("candles", "1h"));

        var plan = CollectionPlanBuilder.Build(state, [], [], NoRecorded());

        Assert.Empty(plan.Assets);
        Assert.Single(plan.Blocked);
        Assert.Contains("instrument precision unknown", plan.Blocked[0].Reason);
        Assert.Equal("BTCUSDT_perp", plan.Blocked[0].Dir);
        Assert.Equal("BTC/USDT", plan.Blocked[0].Canonical);
    }

    [Fact]
    public void Build_EffectiveStart_ClampsToEarliestDiscoveredAcrossIntervals()
    {
        var state = State(T("mark-price", "1h", start: "2020-01"));
        var discovered = new List<DiscoveredFirstMonthRow>
        {
            new("binance", "BTCUSDT_perp", "mark-price", "1h", "2023-05"),
            new("binance", "BTCUSDT_perp", "mark-price", "5m", "2023-04"),  // earlier across intervals
        };

        var plan = CollectionPlanBuilder.Build(state, discovered, Meta(2), NoRecorded());

        Assert.Single(plan.Assets);
        Assert.Equal(new DateOnly(2023, 4, 1), plan.Assets[0].Feeds[0].EffectiveStart);
    }

    [Fact]
    public void Build_NoDiscovery_KeepsHistoryStart()
    {
        var state = State(T("candles", "1h", start: "2020-01"));

        var plan = CollectionPlanBuilder.Build(state, [], Meta(2), NoRecorded());

        Assert.Single(plan.Assets);
        Assert.Equal(new DateOnly(2020, 1, 1), plan.Assets[0].Feeds[0].EffectiveStart);
    }
}
