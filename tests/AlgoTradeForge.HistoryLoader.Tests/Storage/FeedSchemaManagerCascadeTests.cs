using AlgoTradeForge.Domain.History;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Infrastructure.Storage;
using AlgoTradeForge.Infrastructure.IO;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Storage;

/// <summary>P1b-39 / P1b-41 — multi-entry atomic rewrite for cascade delete.</summary>
public sealed class FeedSchemaManagerCascadeTests : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), $"FeedSchemaManagerCascadeTests_{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private string AssetDir(string name) => Path.Combine(_tempDir, name);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AltBarFeedSpec ParentSpec(string sidecarFeedId) => new(
        Kind: "OHLCV_AltBar",
        Columns: ["ts", "o", "h", "l", "c", "vol"],
        Type: new AggregatedTypeInfo { Code = "EqIV", Name = "EqualImbalance" },
        Source: new AggregatedSourceInfo { Feed = "1m", RecordCount = 1000 },
        Threshold: new ThresholdInfo
        {
            Value = 500m, Unit = "base_asset", InputMode = "absolute",
        },
        Build: new BuildInfo { ToolVersion = "test" },
        Fidelity: new FidelityInfo { ImbalanceReconstructionMethod = "tick_signed" },
        FirstBarTs: null,
        LastBarTs: null,
        Sidecar: sidecarFeedId);

    private static AltBarFeedSpec SidecarSpec() => new(
        Kind: "Side",
        Columns: ["ts", "signed_imbalance", "buy_volume", "sell_volume", "realized_threshold"],
        Type: new AggregatedTypeInfo { Code = "EqIV" },
        Source: new AggregatedSourceInfo { Feed = "1m" },
        Threshold: new ThresholdInfo { Value = 500m, Unit = "base_asset", InputMode = "absolute" },
        Build: new BuildInfo { ToolVersion = "test" },
        Fidelity: new FidelityInfo { ImbalanceReconstructionMethod = "tick_signed" },
        FirstBarTs: null,
        LastBarTs: null,
        Sidecar: null);

    [Fact]
    public async Task RemoveFeedAndSidecar_BothEntriesGoneAtomically()
    {
        var manager = new FeedSchemaManager(new LocalFileStorage());
        var assetDir = AssetDir("BTCUSDT_perp");
        await manager.EnsureAltBarFeed(assetDir, "EqIV_1m_500", ParentSpec("EqIV_1m_500.flow"), Ct);
        await manager.EnsureAltBarFeed(assetDir, "EqIV_1m_500.flow", SidecarSpec(), Ct);

        var before = (await manager.Load(assetDir, Ct))!;
        Assert.Contains("EqIV_1m_500", before.Feeds);
        Assert.Contains("EqIV_1m_500.flow", before.Feeds);

        await manager.RemoveFeedAndSidecar(assetDir, "EqIV_1m_500", "EqIV_1m_500.flow", Ct);

        var after = (await manager.Load(assetDir, Ct))!;
        Assert.DoesNotContain("EqIV_1m_500", after.Feeds);
        Assert.DoesNotContain("EqIV_1m_500.flow", after.Feeds);
    }

    [Fact]
    public async Task RemoveFeed_SingleEntry_LeavesOthersIntact()
    {
        var manager = new FeedSchemaManager(new LocalFileStorage());
        var assetDir = AssetDir("BTCUSDT_perp");
        await manager.EnsureSchema(assetDir, "1m", "1m", ["ts", "o", "h", "l", "c", "vol"], ct: Ct);
        await manager.EnsureAltBarFeed(assetDir, "EqV_1m_1000", ParentSpec(sidecarFeedId: null!) with { Sidecar = null }, Ct);

        await manager.RemoveFeed(assetDir, "EqV_1m_1000", Ct);

        var after = (await manager.Load(assetDir, Ct))!;
        Assert.DoesNotContain("EqV_1m_1000", after.Feeds);
        Assert.Contains("1m", after.Feeds);
    }

    [Fact]
    public async Task RemoveFeed_NonExistentEntry_NoOp_NoEvent()
    {
        var manager = new FeedSchemaManager(new LocalFileStorage());
        var assetDir = AssetDir("BTCUSDT_perp");
        await manager.EnsureSchema(assetDir, "1m", "1m", ["ts", "o", "h", "l", "c", "vol"], ct: Ct);

        var changeCount = 0;
        manager.ManifestChanged += _ => changeCount++;

        await manager.RemoveFeed(assetDir, "DoesNotExist_1m_1", Ct);

        Assert.Equal(0, changeCount);   // no event raised when nothing changed
    }

    [Fact]
    public async Task RemoveFeedAndSidecar_RaisesManifestChangedOnce()
    {
        var manager = new FeedSchemaManager(new LocalFileStorage());
        var assetDir = AssetDir("BTCUSDT_perp");
        await manager.EnsureAltBarFeed(assetDir, "EqIV_1m_500", ParentSpec("EqIV_1m_500.flow"), Ct);
        await manager.EnsureAltBarFeed(assetDir, "EqIV_1m_500.flow", SidecarSpec(), Ct);

        var changeCount = 0;
        manager.ManifestChanged += _ => changeCount++;

        await manager.RemoveFeedAndSidecar(assetDir, "EqIV_1m_500", "EqIV_1m_500.flow", Ct);

        Assert.Equal(1, changeCount);   // single rewrite → single event
    }
}
