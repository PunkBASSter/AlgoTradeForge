using AlgoTradeForge.Domain.History;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Infrastructure.Storage;
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

    private static AltBarFeedSpec ParentSpec(string sidecarFeedId) => new(
        Kind: "OHLCV_AltBar",
        Columns: ["ts", "o", "h", "l", "c", "vol"],
        Type: new AggregatedTypeInfo { Code = "EqI", Name = "EqualImbalance" },
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
        Type: new AggregatedTypeInfo { Code = "EqI" },
        Source: new AggregatedSourceInfo { Feed = "1m" },
        Threshold: new ThresholdInfo { Value = 500m, Unit = "base_asset", InputMode = "absolute" },
        Build: new BuildInfo { ToolVersion = "test" },
        Fidelity: new FidelityInfo { ImbalanceReconstructionMethod = "tick_signed" },
        FirstBarTs: null,
        LastBarTs: null,
        Sidecar: null);

    [Fact]
    public void RemoveFeedAndSidecar_BothEntriesGoneAtomically()
    {
        var manager = new FeedSchemaManager();
        var assetDir = AssetDir("BTCUSDT_perp");
        manager.EnsureAltBarFeed(assetDir, "EqI_1m_500", ParentSpec("EqI_1m_500.flow"));
        manager.EnsureAltBarFeed(assetDir, "EqI_1m_500.flow", SidecarSpec());

        var before = manager.Load(assetDir)!;
        Assert.Contains("EqI_1m_500", before.Feeds);
        Assert.Contains("EqI_1m_500.flow", before.Feeds);

        manager.RemoveFeedAndSidecar(assetDir, "EqI_1m_500", "EqI_1m_500.flow");

        var after = manager.Load(assetDir)!;
        Assert.DoesNotContain("EqI_1m_500", after.Feeds);
        Assert.DoesNotContain("EqI_1m_500.flow", after.Feeds);
    }

    [Fact]
    public void RemoveFeed_SingleEntry_LeavesOthersIntact()
    {
        var manager = new FeedSchemaManager();
        var assetDir = AssetDir("BTCUSDT_perp");
        manager.EnsureSchema(assetDir, "1m", "1m", ["ts", "o", "h", "l", "c", "vol"]);
        manager.EnsureAltBarFeed(assetDir, "EqV_1m_1000", ParentSpec(sidecarFeedId: null!) with { Sidecar = null });

        manager.RemoveFeed(assetDir, "EqV_1m_1000");

        var after = manager.Load(assetDir)!;
        Assert.DoesNotContain("EqV_1m_1000", after.Feeds);
        Assert.Contains("1m", after.Feeds);
    }

    [Fact]
    public void RemoveFeed_NonExistentEntry_NoOp_NoEvent()
    {
        var manager = new FeedSchemaManager();
        var assetDir = AssetDir("BTCUSDT_perp");
        manager.EnsureSchema(assetDir, "1m", "1m", ["ts", "o", "h", "l", "c", "vol"]);

        var changeCount = 0;
        manager.ManifestChanged += _ => changeCount++;

        manager.RemoveFeed(assetDir, "DoesNotExist_1m_1");

        Assert.Equal(0, changeCount);   // no event raised when nothing changed
    }

    [Fact]
    public void RemoveFeedAndSidecar_RaisesManifestChangedOnce()
    {
        var manager = new FeedSchemaManager();
        var assetDir = AssetDir("BTCUSDT_perp");
        manager.EnsureAltBarFeed(assetDir, "EqI_1m_500", ParentSpec("EqI_1m_500.flow"));
        manager.EnsureAltBarFeed(assetDir, "EqI_1m_500.flow", SidecarSpec());

        var changeCount = 0;
        manager.ManifestChanged += _ => changeCount++;

        manager.RemoveFeedAndSidecar(assetDir, "EqI_1m_500", "EqI_1m_500.flow");

        Assert.Equal(1, changeCount);   // single rewrite → single event
    }
}
