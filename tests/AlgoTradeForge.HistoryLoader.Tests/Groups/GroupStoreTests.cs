using System.Text.Json;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Archive;
using AlgoTradeForge.HistoryLoader.Application.Groups;
using AlgoTradeForge.HistoryLoader.Domain;
using AlgoTradeForge.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Groups;

public sealed class GroupStoreTests : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), $"GroupStoreTests_{Guid.NewGuid():N}");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private GroupStore MakeStore(ArchiveMaterializerRegistry? materializers = null) =>
        new(new LocalFileStorage(),
            Options.Create(new HistoryLoaderOptions { ConfigRoot = _tempDir }),
            materializers ?? new ArchiveMaterializerRegistry([]),
            NullLogger<GroupStore>.Instance);

    private static ArchiveMaterializerRegistry RegistryFor(string exchange, string feed)
    {
        var m = Substitute.For<IArchiveMaterializer>();
        m.Exchange.Returns(exchange);
        m.FeedName.Returns(feed);
        m.Supports(Arg.Any<string>()).Returns(true);
        return new ArchiveMaterializerRegistry([m]);
    }

    private static CollectionGroup PerpGroup(string name, params (string Feed, string Collect)[] feeds) => new(
        Name:            name,
        Enabled:         true,
        Exchanges:       ["binance"],
        Assets:          new GroupAssets(["BTC/USDT-PERP"], "2023-01"),
        Feeds:           feeds.ToDictionary(
            f => f.Feed,
            f => new GroupFeed(f.Collect, f.Feed == FeedNames.Candles ? ["1h"] : null, null)),
        Derived:         null,
        SymbolOverrides: null);

    private static CollectionGroup ValidGroup(string name) => new(
        Name:            name,
        Enabled:         true,
        Exchanges:       ["binance"],
        Assets:          new GroupAssets(["BTC/USDT"], "2023-01"),
        Feeds:           new Dictionary<string, GroupFeed>
        {
            ["candles"] = new GroupFeed("eager", ["1h"], null),
        },
        Derived:         null,
        SymbolOverrides: null);

    // -------------------------------------------------------------------------
    // Round-trip + ETag change

    [Fact]
    public async Task Put_Get_RoundTrip_ReturnsSameGroup()
    {
        var store = MakeStore();
        var group = ValidGroup("my-group");

        var etag1 = await store.Put("my-group", group, expectedETag: null, Ct);

        var doc = await store.Get("my-group", Ct);

        Assert.NotNull(doc);
        Assert.Equal(etag1, doc.ETag);
        Assert.Equal(group.Name,    doc.Group.Name);
        Assert.Equal(group.Enabled, doc.Group.Enabled);
    }

    [Fact]
    public async Task Put_Twice_ETagChanges()
    {
        var store = MakeStore();
        var group = ValidGroup("my-group");

        var etag1 = await store.Put("my-group", group, expectedETag: null, Ct);
        var etag2 = await store.Put("my-group", group with { Enabled = false }, expectedETag: etag1, Ct);

        Assert.NotEqual(etag1, etag2);
    }

    // -------------------------------------------------------------------------
    // Stale-ETag → ConcurrencyConflictException

    [Fact]
    public async Task Put_StaleETag_ThrowsConcurrencyConflict()
    {
        var store = MakeStore();
        var group = ValidGroup("alpha");

        await store.Put("alpha", group, expectedETag: null, Ct);

        await Assert.ThrowsAsync<ConcurrencyConflictException>(() =>
            store.Put("alpha", group, expectedETag: "stale-etag", Ct));
    }

    // -------------------------------------------------------------------------
    // Create-when-exists (null etag) → conflict

    [Fact]
    public async Task Put_CreateWhenExists_ThrowsConcurrencyConflict()
    {
        var store = MakeStore();
        var group = ValidGroup("beta");

        await store.Put("beta", group, expectedETag: null, Ct);

        await Assert.ThrowsAsync<ConcurrencyConflictException>(() =>
            store.Put("beta", group, expectedETag: null, Ct));
    }

    // -------------------------------------------------------------------------
    // Invalid group → GroupValidationException, file NOT written

    [Fact]
    public async Task Put_InvalidGroup_ThrowsGroupValidationException_NoFileWritten()
    {
        var store = MakeStore();
        var bad = new CollectionGroup(
            Name:            "gamma",
            Enabled:         true,
            Exchanges:       [],       // invalid: empty exchanges
            Assets:          new GroupAssets([], "bad-date"),
            Feeds:           new Dictionary<string, GroupFeed>(),
            Derived:         null,
            SymbolOverrides: null);

        var ex = await Assert.ThrowsAsync<GroupValidationException>(() =>
            store.Put("gamma", bad, expectedETag: null, Ct));

        Assert.NotEmpty(ex.Errors);

        var groupsDir = Path.Combine(_tempDir, "groups");
        Assert.False(File.Exists(Path.Combine(groupsDir, "gamma.json")));
    }

    // -------------------------------------------------------------------------
    // Collectability: on-demand feed with no archive materializer is rejected (would never collect)

    [Fact]
    public async Task Put_OnDemandNonReplenishableFeed_ThrowsGroupValidationException_NoFileWritten()
    {
        var store = MakeStore();   // empty registry → liquidations is not replenishable
        var group = PerpGroup("liq-group",
            (FeedNames.Candles, "eager"),
            (FeedNames.Liquidations, "on-demand"));

        var ex = await Assert.ThrowsAsync<GroupValidationException>(() =>
            store.Put("liq-group", group, expectedETag: null, Ct));

        Assert.Contains(ex.Errors, e => e.Contains(FeedNames.Liquidations) && e.Contains("on-demand"));

        var groupsDir = Path.Combine(_tempDir, "groups");
        Assert.False(File.Exists(Path.Combine(groupsDir, "liq-group.json")));
    }

    [Fact]
    public async Task Put_OnDemandReplenishableFeed_Succeeds()
    {
        var store = MakeStore(RegistryFor("binance", FeedNames.Ticks));
        var group = PerpGroup("ticks-group",
            (FeedNames.Candles, "eager"),
            (FeedNames.Ticks, "on-demand"));

        var etag = await store.Put("ticks-group", group, expectedETag: null, Ct);

        Assert.NotNull(etag);
        Assert.NotNull(await store.Get("ticks-group", Ct));
    }

    // -------------------------------------------------------------------------
    // Name mismatch rejected

    [Fact]
    public async Task Put_NameMismatch_ThrowsGroupValidationException()
    {
        var store = MakeStore();
        var group = ValidGroup("wrong-name");

        var ex = await Assert.ThrowsAsync<GroupValidationException>(() =>
            store.Put("delta", group, expectedETag: null, Ct));

        Assert.Contains(ex.Errors, e => e.Contains("wrong-name") || e.Contains("delta"));
    }

    // -------------------------------------------------------------------------
    // List skips corrupt file, returns healthy ones

    [Fact]
    public async Task List_SkipsCorruptFile_ReturnsHealthyOnes()
    {
        var store = MakeStore();
        var good = ValidGroup("healthy");
        await store.Put("healthy", good, expectedETag: null, Ct);

        // Write a corrupt JSON file directly
        var groupsDir = Path.Combine(_tempDir, "groups");
        Directory.CreateDirectory(groupsDir);
        await File.WriteAllTextAsync(Path.Combine(groupsDir, "corrupt.json"), "not-valid-json{{{{", Ct);

        var list = await store.List(Ct);

        Assert.Single(list);
        Assert.Equal("healthy", list[0].Group.Name);
    }

    // -------------------------------------------------------------------------
    // GroupsChanged fires on Put and Delete

    [Fact]
    public async Task GroupsChanged_FiresOnPut()
    {
        var store = MakeStore();
        var group = ValidGroup("epsilon");
        var fired = 0;
        store.GroupsChanged += () => fired++;

        await store.Put("epsilon", group, expectedETag: null, Ct);

        Assert.Equal(1, fired);
    }

    [Fact]
    public async Task GroupsChanged_FiresOnDelete()
    {
        var store = MakeStore();
        var group = ValidGroup("zeta");
        await store.Put("zeta", group, expectedETag: null, Ct);
        var fired = 0;
        store.GroupsChanged += () => fired++;

        await store.Delete("zeta", Ct);

        Assert.Equal(1, fired);
    }

    [Fact]
    public async Task GroupsChanged_DoesNotFireOnDeleteMiss()
    {
        var store = MakeStore();
        var fired = 0;
        store.GroupsChanged += () => fired++;

        var deleted = await store.Delete("no-such-group", Ct);

        Assert.False(deleted);
        Assert.Equal(0, fired);
    }

    // -------------------------------------------------------------------------
    // Delete returns false when group absent, true when it existed

    [Fact]
    public async Task Delete_ExistingGroup_ReturnsTrueAndGroupGone()
    {
        var store = MakeStore();
        var group = ValidGroup("eta");
        await store.Put("eta", group, expectedETag: null, Ct);

        var deleted = await store.Delete("eta", Ct);

        Assert.True(deleted);
        Assert.Null(await store.Get("eta", Ct));
    }

    [Fact]
    public async Task Delete_NonExistentGroup_ReturnsFalse()
    {
        var store = MakeStore();

        var deleted = await store.Delete("theta", Ct);

        Assert.False(deleted);
    }

    // -------------------------------------------------------------------------
    // Get on corrupt JSON throws GroupValidationException

    [Fact]
    public async Task Get_CorruptJson_ThrowsGroupValidationException()
    {
        var store = MakeStore();
        // Write a corrupt JSON file directly
        var groupsDir = Path.Combine(_tempDir, "groups");
        Directory.CreateDirectory(groupsDir);
        await File.WriteAllTextAsync(Path.Combine(groupsDir, "bad-group.json"), "{not:valid{{", Ct);

        var ex = await Assert.ThrowsAsync<GroupValidationException>(
            () => store.Get("bad-group", Ct));

        Assert.NotEmpty(ex.Errors);
        Assert.Contains(ex.Errors, e => e.Contains("bad-group.json"));
    }

    // -------------------------------------------------------------------------
    // Name guard: bad names throw ArgumentException on every public method

    [Theory]
    [InlineData("")]
    [InlineData("../evil")]
    [InlineData("Has Spaces")]
    [InlineData("UPPERCASE")]
    public async Task PublicMethods_BadName_ThrowArgumentException(string badName)
    {
        var store = MakeStore();
        var group = ValidGroup("placeholder");

        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            store.Get(badName, Ct));
        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            store.Put(badName, group, expectedETag: null, Ct));
        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            store.Delete(badName, Ct));
    }
}
