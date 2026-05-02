using System.Text.Json;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.Subscriptions;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Strategy.Subscriptions;

/// <summary>
/// P4-7 — Polymorphic deserialization round-trip across all four subtypes.
/// Pins that <c>System.Text.Json</c> emits + accepts the <c>"kind"</c> discriminator
/// without bespoke converters; that the wire never carries a redundant <c>Kind</c>
/// field (it's <c>[JsonIgnore]</c>'d on the C# side); and that mixed lists deserialize
/// per-element correctly.
/// </summary>
public class DataFeedSubscriptionPolymorphismTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    [Fact]
    public void TimeBarSubscription_RoundTripsThroughJson()
    {
        var original = new TimeBarSubscription("BTCUSDT_perp", "binance-futures", DataFeedRole.Primary, TimeFrame.Parse("1m"));

        var json = JsonSerializer.Serialize<DataFeedSubscription>(original, Options);
        var roundTripped = JsonSerializer.Deserialize<DataFeedSubscription>(json, Options);

        var tb = Assert.IsType<TimeBarSubscription>(roundTripped);
        Assert.Equal("BTCUSDT_perp", tb.AssetName);
        Assert.Equal("binance-futures", tb.Exchange);
        Assert.Equal(DataFeedRole.Primary, tb.Role);
        Assert.Equal("1m", tb.TimeFrame.Code);
    }

    [Fact]
    public void AltBarSubscription_RoundTripsThroughJson()
    {
        var original = new AltBarSubscription("BTCUSDT_perp", "binance-futures", DataFeedRole.Primary, "EqV_1m_500m");

        var json = JsonSerializer.Serialize<DataFeedSubscription>(original, Options);
        var roundTripped = JsonSerializer.Deserialize<DataFeedSubscription>(json, Options);

        var ab = Assert.IsType<AltBarSubscription>(roundTripped);
        Assert.Equal("EqV_1m_500m", ab.FeedId);
    }

    [Fact]
    public void TickSubscription_RoundTripsThroughJson()
    {
        var original = new TickSubscription("BTCUSDT_perp", "binance-futures", DataFeedRole.Primary);

        var json = JsonSerializer.Serialize<DataFeedSubscription>(original, Options);
        var roundTripped = JsonSerializer.Deserialize<DataFeedSubscription>(json, Options);

        Assert.IsType<TickSubscription>(roundTripped);
    }

    [Fact]
    public void SideFeedSubscription_RoundTripsThroughJson()
    {
        var original = new SideFeedSubscription("BTCUSDT_perp", "binance-futures", DataFeedRole.Side, "funding-rate");

        var json = JsonSerializer.Serialize<DataFeedSubscription>(original, Options);
        var roundTripped = JsonSerializer.Deserialize<DataFeedSubscription>(json, Options);

        var s = Assert.IsType<SideFeedSubscription>(roundTripped);
        Assert.Equal("funding-rate", s.FeedId);
        Assert.Equal(DataFeedRole.Side, s.Role);
    }

    [Fact]
    public void DiscriminatorPropertyName_OnWireIs_kind()
    {
        var sub = new TimeBarSubscription("BTC", "ex", DataFeedRole.Primary, TimeFrame.Parse("1h"));
        var json = JsonSerializer.Serialize<DataFeedSubscription>(sub, Options);
        using var doc = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("kind", out var kindEl));
        Assert.Equal("TimeBar", kindEl.GetString());
    }

    [Fact]
    public void OnlyOneKindField_OnWire()
    {
        // The type hierarchy IS the discriminator (no C# Kind property), so the wire carries
        // exactly one "kind" entry — owned by [JsonPolymorphic]. Pin this so a future addition
        // of a redundant Kind property would fail loudly here instead of producing a duplicate-
        // key serialization error at runtime.
        var sub = new AltBarSubscription("BTC", "ex", DataFeedRole.Primary, "EqV_1m_1k");
        var json = JsonSerializer.Serialize<DataFeedSubscription>(sub, Options);

        // Use JsonDocument over substring counting so an unrelated property containing "kind"
        // (e.g. a hypothetical "kindOf" or an asset-name with "kind" inside) can't false-match.
        using var doc = JsonDocument.Parse(json);
        var kindCount = doc.RootElement.EnumerateObject().Count(p => p.Name == "kind");
        Assert.Equal(1, kindCount);
    }

    [Fact]
    public void MissingDiscriminator_DeserializeThrows()
    {
        const string json = "{\"assetName\":\"BTC\",\"exchange\":\"ex\",\"role\":0,\"timeFrame\":\"1m\"}";

        // STJ throws NotSupportedException when an abstract base lands in ObjectDefaultConverter
        // without a discriminator. JsonException is for malformed JSON; the abstract-type case
        // is "not supported" semantically. Either signals failure to the API boundary — but
        // narrow the catch so genuinely unrelated failures (OOM, infra) surface untouched.
        var ex = Assert.ThrowsAny<Exception>(() =>
            JsonSerializer.Deserialize<DataFeedSubscription>(json, Options));
        Assert.True(
            ex is NotSupportedException or JsonException,
            $"Expected NotSupportedException or JsonException, got {ex.GetType().Name}: {ex.Message}");
    }

    [Fact]
    public void UnknownDiscriminator_DeserializeThrows()
    {
        const string json = "{\"kind\":\"Renko\",\"assetName\":\"BTC\",\"exchange\":\"ex\",\"role\":0}";

        Assert.ThrowsAny<JsonException>(() =>
            JsonSerializer.Deserialize<DataFeedSubscription>(json, Options));
    }

    [Fact]
    public void MixedList_DeserializesPerElement()
    {
        var subs = new List<DataFeedSubscription>
        {
            new TimeBarSubscription("BTC", "ex", DataFeedRole.Primary, TimeFrame.Parse("1m")),
            new AltBarSubscription("BTC", "ex", DataFeedRole.Side, "EqV_1m_500m"),
            new TickSubscription("BTC", "ex", DataFeedRole.Side),
            new SideFeedSubscription("BTC", "ex", DataFeedRole.Side, "funding-rate"),
        };

        var json = JsonSerializer.Serialize(subs, Options);
        var roundTripped = JsonSerializer.Deserialize<List<DataFeedSubscription>>(json, Options);

        Assert.NotNull(roundTripped);
        Assert.Equal(4, roundTripped!.Count);
        Assert.IsType<TimeBarSubscription>(roundTripped[0]);
        Assert.IsType<AltBarSubscription>(roundTripped[1]);
        Assert.IsType<TickSubscription>(roundTripped[2]);
        Assert.IsType<SideFeedSubscription>(roundTripped[3]);
    }

    [Fact]
    public void Role_WithDomainDefaultOptions_SerializesAsNumber()
    {
        // Pins the *Domain default* wire shape: with plain JsonSerializerDefaults.Web
        // (no JsonStringEnumConverter), DataFeedRole serializes as int. The actual
        // FE-bound wire contract uses Application's JsonDefaults.Api which adds the
        // string converter — see DataFeedRoleWireShapeTests in Application.Tests for
        // the FE-bound contract assertion.
        var sub = new TimeBarSubscription("BTC", "ex", DataFeedRole.Side, TimeFrame.Parse("1m"));
        var json = JsonSerializer.Serialize<DataFeedSubscription>(sub, Options);
        using var doc = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("role", out var roleEl));
        Assert.Equal(JsonValueKind.Number, roleEl.ValueKind);
        Assert.Equal((int)DataFeedRole.Side, roleEl.GetInt32());
    }

    [Fact]
    public void TimeBar_DistinctTimeFrames_AreNotEqual_AndCoexistInHashSet()
    {
        // Locks the dedup contract: two TimeBarSubscriptions differing ONLY by TimeFrame
        // (same asset+exchange+role) are unequal and both survive a HashSet. Pins TimeFrame
        // as part of the auto-generated record equality. A future refactor that moves
        // TimeFrame out of the primary constructor would silently merge "1m" and "5m"
        // primaries — this test surfaces that as a failure.
        var oneMinute = new TimeBarSubscription("BTC", "ex", DataFeedRole.Primary, TimeFrame.Parse("1m"));
        var fiveMinute = new TimeBarSubscription("BTC", "ex", DataFeedRole.Primary, TimeFrame.Parse("5m"));

        Assert.NotEqual(oneMinute, fiveMinute);

        var set = new HashSet<TimeBarSubscription> { oneMinute, fiveMinute };
        Assert.Equal(2, set.Count);
    }

    [Fact]
    public void SideFeedSubscription_RejectsNonSideRole()
    {
        // Single-instance invariant: SideFeedSubscription with anything other than Role.Side
        // is nonsensical and the ctor should refuse to construct it. (Cross-subscription
        // invariants like "exactly one Primary in the set" still live in BacktestPreparer.)
        Assert.Throws<ArgumentException>(() =>
            new SideFeedSubscription("BTC", "ex", DataFeedRole.Primary, "funding-rate"));
    }

    [Fact]
    public void Polymorphic_DistinctConcreteTypes_AreNotEqual_EvenWithSamePayload()
    {
        // A TimeBarSubscription and an AltBarSubscription with overlapping (asset, exchange,
        // role) but different concrete types must NOT compare equal — record-equality factors
        // in EqualityContract (the runtime type). Otherwise an optimization fan-out group
        // mixing time bars and alt bars could silently dedup across kinds.
        var tb = new TimeBarSubscription("BTC", "ex", DataFeedRole.Primary, TimeFrame.Parse("1m"));
        var ab = new AltBarSubscription("BTC", "ex", DataFeedRole.Primary, "EqV_1m_500m");

        DataFeedSubscription tbBase = tb;
        DataFeedSubscription abBase = ab;

        Assert.NotEqual(tbBase, abBase);

        var set = new HashSet<DataFeedSubscription> { tbBase, abBase };
        Assert.Equal(2, set.Count);
    }
}
