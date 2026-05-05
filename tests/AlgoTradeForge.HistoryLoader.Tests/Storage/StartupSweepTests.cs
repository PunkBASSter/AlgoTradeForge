using AlgoTradeForge.Domain.History;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Application.Aggregation;
using AlgoTradeForge.HistoryLoader.Tests.TestHelpers;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Storage;

/// <summary>
/// P1a-13 → P1a-17, X-4: <see cref="AggregatedDirSweeper"/> behavior on a clean / dirty layout.
/// Tests exercise the sweeper directly. The <c>StartupSweepService</c> hosted-service wrapper
/// is a thin walk over <c>{dataRoot}/{exchange}/{asset}/</c> dirs that calls Sweep on each.
/// </summary>
public sealed class StartupSweepTests : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), $"StartupSweepTests_{Guid.NewGuid():N}");

    private readonly ISchemaManager _schema = Substitute.For<ISchemaManager>();
    private readonly ListLogger<AggregatedDirSweeper> _logger = new();

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private string AssetDir(string name)
    {
        var path = Path.Combine(_tempDir, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private AggregatedDirSweeper BuildSweeper() => new(_schema, _logger);

    private static FeedMetadata MetadataWith(params string[] feedIds)
    {
        var feeds = feedIds.ToDictionary(
            id => id,
            id => new FeedDefinition { Kind = "aggregated", Columns = [] },
            StringComparer.Ordinal);
        return new FeedMetadata { Feeds = feeds };
    }

    // -------------------------------------------------------------------------
    // P1a-14 — orphan *.tmp deleted; real partitions untouched
    // -------------------------------------------------------------------------

    [Fact]
    public void Sweep_DeletesOrphanTmpFiles_LeavesRealPartitionsUntouched()
    {
        var assetDir = AssetDir("BTCUSDT_TmpClean");
        var feedDir = Path.Combine(assetDir, "aggregated", "EqV_1m_1000");
        var flowDir = Path.Combine(assetDir, "aggregated", "EqV_1m_1000.flow");
        Directory.CreateDirectory(feedDir);
        Directory.CreateDirectory(flowDir);

        // Real partition files (must survive)
        File.WriteAllText(Path.Combine(feedDir, "2026-04.csv"), "ts,o,h,l,c,vol\n");
        File.WriteAllText(Path.Combine(flowDir, "2026-04.csv"), "ts,signed_imbalance\n");

        // Orphan .tmp files (must be deleted)
        File.WriteAllText(Path.Combine(feedDir, "2026-04.csv.tmp"), "garbage");
        File.WriteAllText(Path.Combine(flowDir, "2026-04.csv.tmp"), "garbage");

        _schema.Load(assetDir).Returns(MetadataWith("EqV_1m_1000", "EqV_1m_1000.flow"));

        BuildSweeper().Sweep(assetDir);

        Assert.True(File.Exists(Path.Combine(feedDir, "2026-04.csv")));
        Assert.True(File.Exists(Path.Combine(flowDir, "2026-04.csv")));
        Assert.False(File.Exists(Path.Combine(feedDir, "2026-04.csv.tmp")));
        Assert.False(File.Exists(Path.Combine(flowDir, "2026-04.csv.tmp")));
    }

    // -------------------------------------------------------------------------
    // P1a-15 — orphan feed dir deleted + WARN log with absolute path
    // -------------------------------------------------------------------------

    [Fact]
    public void Sweep_DeletesOrphanFeedDir_AndLogsWarningWithAbsolutePath()
    {
        var assetDir = AssetDir("BTCUSDT_OrphanFeed");
        var orphanDir = Path.Combine(assetDir, "aggregated", "Orphan_1m_999");
        Directory.CreateDirectory(orphanDir);
        File.WriteAllText(Path.Combine(orphanDir, "2026-04.csv"), "ts,o,h,l,c,vol\n");

        // The manifest knows nothing about Orphan_1m_999.
        _schema.Load(assetDir).Returns(MetadataWith("EqV_1m_1000"));

        BuildSweeper().Sweep(assetDir);

        Assert.False(Directory.Exists(orphanDir));

        var warning = Assert.Single(_logger.Warnings);
        Assert.Contains("orphan aggregated dir", warning.Message, StringComparison.Ordinal);
        Assert.Contains(Path.GetFullPath(orphanDir), warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Sweep_DeletesOrphanFlowSidecarDir()
    {
        // A .flow sidecar dir is its own feed-id in feeds.json. If absent from the
        // manifest, treat as orphan (just like a bar feed dir).
        var assetDir = AssetDir("BTCUSDT_OrphanFlow");
        var orphanFlow = Path.Combine(assetDir, "aggregated", "EqIV_ticks_500000.flow");
        Directory.CreateDirectory(orphanFlow);
        File.WriteAllText(Path.Combine(orphanFlow, "2026-04.csv"),
            "ts,signed_imbalance,buy_volume,sell_volume,realized_threshold\n");

        // Manifest has neither EqIV_ticks_500000 nor its .flow sidecar.
        _schema.Load(assetDir).Returns(MetadataWith());

        BuildSweeper().Sweep(assetDir);

        Assert.False(Directory.Exists(orphanFlow));
    }

    // -------------------------------------------------------------------------
    // P1a-16 — orphan .staging-*/ deleted EVEN when feedId is in manifest
    // -------------------------------------------------------------------------

    [Fact]
    public void Sweep_DeletesStagingDir_EvenWhenManifestEntryExists()
    {
        var assetDir = AssetDir("BTCUSDT_Staging");
        var feedDir = Path.Combine(assetDir, "aggregated", "EqV_1m_1000");
        var stagingDir = Path.Combine(feedDir, ".staging-job-123");
        Directory.CreateDirectory(stagingDir);
        File.WriteAllText(Path.Combine(stagingDir, "2026-04.csv"), "ts,o,h,l,c,vol\n");
        File.WriteAllText(Path.Combine(feedDir, "2026-03.csv"), "ts,o,h,l,c,vol\n");

        _schema.Load(assetDir).Returns(MetadataWith("EqV_1m_1000"));

        BuildSweeper().Sweep(assetDir);

        Assert.False(Directory.Exists(stagingDir));
        Assert.True(Directory.Exists(feedDir));
        Assert.True(File.Exists(Path.Combine(feedDir, "2026-03.csv")));
    }

    // -------------------------------------------------------------------------
    // P1a-17 — manifest entry without a dir is preserved (asymmetry)
    // -------------------------------------------------------------------------

    [Fact]
    public void Sweep_ManifestEntryWithoutDir_DoesNotMutateManifest()
    {
        // The manifest declares EqV_1m_2000 but no directory was created for it.
        // The sweeper MUST NOT touch the manifest in this direction — only the
        // dir → manifest direction is destructive (TRD §4.1).
        var assetDir = AssetDir("BTCUSDT_Asymmetry");
        Directory.CreateDirectory(Path.Combine(assetDir, "aggregated"));

        _schema.Load(assetDir).Returns(MetadataWith("EqV_1m_2000"));

        BuildSweeper().Sweep(assetDir);

        // Sweeper performs a Load (read), but never calls EnsureSchema or any
        // other mutator on the manifest.
        _schema.Received().Load(assetDir);
        _schema.DidNotReceive().EnsureSchema(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string[]>(), Arg.Any<AutoApplySpec?>());
        _schema.DidNotReceive().EnsureCandleConfig(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>());
    }

    // -------------------------------------------------------------------------
    // No-op cases
    // -------------------------------------------------------------------------

    [Fact]
    public void Sweep_NoAggregatedDir_IsNoOp()
    {
        var assetDir = AssetDir("BTCUSDT_NoAgg");

        BuildSweeper().Sweep(assetDir);

        Assert.Empty(_logger.Entries);
    }

    [Fact]
    public void Sweep_AssetWithNoFeedsJson_ProcessesEverythingAsOrphan()
    {
        var assetDir = AssetDir("BTCUSDT_NoFeedsJson");
        var feedDir = Path.Combine(assetDir, "aggregated", "EqV_1m_1000");
        Directory.CreateDirectory(feedDir);
        File.WriteAllText(Path.Combine(feedDir, "2026-04.csv"), "ts,o,h,l,c,vol\n");

        _schema.Load(assetDir).Returns((FeedMetadata?)null);

        BuildSweeper().Sweep(assetDir);

        Assert.False(Directory.Exists(feedDir));
        Assert.Single(_logger.Warnings);
    }

    // -------------------------------------------------------------------------
    // Q-4 — bare-vs-pNN collision: sweeper logs WARN but does NOT delete.
    // -------------------------------------------------------------------------

    [Fact]
    public void Sweep_FeedDirHasBareAndPartNumberedCollision_LogsWarn_DoesNotDelete()
    {
        // Operator-induced or migration-induced state: bare and .pNN co-exist for the same
        // month under a known feed. The sweeper observes, logs WARN with both paths, and
        // leaves both files in place — auto-delete would risk masking the underlying bug.
        var assetDir = AssetDir("BTCUSDT_Collision");
        var feedDir = Path.Combine(assetDir, "aggregated", "EqV_1m_1000");
        Directory.CreateDirectory(feedDir);
        File.WriteAllText(Path.Combine(feedDir, "2026-05.csv"), "ts,o,h,l,c,vol\n");
        File.WriteAllText(Path.Combine(feedDir, "2026-05.p01.csv"), "ts,o,h,l,c,vol\n");

        _schema.Load(assetDir).Returns(MetadataWith("EqV_1m_1000"));

        BuildSweeper().Sweep(assetDir);

        // Both files survive — sweeper does not auto-delete on collision.
        Assert.True(File.Exists(Path.Combine(feedDir, "2026-05.csv")));
        Assert.True(File.Exists(Path.Combine(feedDir, "2026-05.p01.csv")));

        // WARN logged, mentions the colliding paths.
        Assert.Contains(_logger.Warnings, w =>
            w.Message.Contains("collision", StringComparison.OrdinalIgnoreCase) &&
            w.Message.Contains(Path.GetFullPath(feedDir), StringComparison.Ordinal));
    }

    [Fact]
    public void Sweep_FeedDirHasOnlyPartNumberedFiles_DoesNotLogCollision()
    {
        // Normal overflow case (.p01 + .p02) must NOT trigger the WARN.
        var assetDir = AssetDir("BTCUSDT_NormalOverflow");
        var feedDir = Path.Combine(assetDir, "aggregated", "EqV_1m_1000");
        Directory.CreateDirectory(feedDir);
        File.WriteAllText(Path.Combine(feedDir, "2026-05.p01.csv"), "ts,o,h,l,c,vol\n");
        File.WriteAllText(Path.Combine(feedDir, "2026-05.p02.csv"), "ts,o,h,l,c,vol\n");

        _schema.Load(assetDir).Returns(MetadataWith("EqV_1m_1000"));

        BuildSweeper().Sweep(assetDir);

        Assert.DoesNotContain(_logger.Warnings, w =>
            w.Message.Contains("collision", StringComparison.OrdinalIgnoreCase));
    }
}
