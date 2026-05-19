using AlgoTradeForge.Domain.History;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Application.Aggregation;
using AlgoTradeForge.HistoryLoader.Tests.TestHelpers;
using AlgoTradeForge.Storage;
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

    private AggregatedDirSweeper BuildSweeper() => new(new LocalFileStorage(), _schema, _logger);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static FeedMetadata MetadataWith(params string[] feedIds)
    {
        var feeds = feedIds.ToDictionary(
            id => id,
            id => new FeedDefinition { Kind = "aggregated", Columns = [] },
            StringComparer.Ordinal);
        return new FeedMetadata { Feeds = feeds };
    }

    [Fact]
    public async Task Sweep_DeletesOrphanTmpFiles_LeavesRealPartitionsUntouched()
    {
        var assetDir = AssetDir("BTCUSDT_TmpClean");
        var feedDir = Path.Combine(assetDir, "aggregated", "EqV_1m_1000");
        var flowDir = Path.Combine(assetDir, "aggregated", "EqV_1m_1000.flow");
        Directory.CreateDirectory(feedDir);
        Directory.CreateDirectory(flowDir);

        File.WriteAllText(Path.Combine(feedDir, "2026-04.csv"), "ts,o,h,l,c,vol\n");
        File.WriteAllText(Path.Combine(flowDir, "2026-04.csv"), "ts,signed_imbalance\n");

        File.WriteAllText(Path.Combine(feedDir, "2026-04.csv.tmp"), "garbage");
        File.WriteAllText(Path.Combine(flowDir, "2026-04.csv.tmp"), "garbage");

        _schema.Load(assetDir, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<FeedMetadata?>(MetadataWith("EqV_1m_1000", "EqV_1m_1000.flow")));

        await BuildSweeper().Sweep(assetDir, Ct);

        Assert.True(File.Exists(Path.Combine(feedDir, "2026-04.csv")));
        Assert.True(File.Exists(Path.Combine(flowDir, "2026-04.csv")));
        Assert.False(File.Exists(Path.Combine(feedDir, "2026-04.csv.tmp")));
        Assert.False(File.Exists(Path.Combine(flowDir, "2026-04.csv.tmp")));
    }

    [Fact]
    public async Task Sweep_DeletesOrphanFeedDir_AndLogsWarningWithAbsolutePath()
    {
        var assetDir = AssetDir("BTCUSDT_OrphanFeed");
        var orphanDir = Path.Combine(assetDir, "aggregated", "Orphan_1m_999");
        Directory.CreateDirectory(orphanDir);
        File.WriteAllText(Path.Combine(orphanDir, "2026-04.csv"), "ts,o,h,l,c,vol\n");

        _schema.Load(assetDir, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<FeedMetadata?>(MetadataWith("EqV_1m_1000")));

        await BuildSweeper().Sweep(assetDir, Ct);

        Assert.False(Directory.Exists(orphanDir));

        var warning = Assert.Single(_logger.Warnings);
        Assert.Contains("orphan aggregated dir", warning.Message, StringComparison.Ordinal);
        Assert.Contains(Path.GetFullPath(orphanDir), warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sweep_DeletesOrphanFlowSidecarDir()
    {
        var assetDir = AssetDir("BTCUSDT_OrphanFlow");
        var orphanFlow = Path.Combine(assetDir, "aggregated", "EqIV_ticks_500000.flow");
        Directory.CreateDirectory(orphanFlow);
        File.WriteAllText(Path.Combine(orphanFlow, "2026-04.csv"),
            "ts,signed_imbalance,buy_volume,sell_volume,realized_threshold\n");

        _schema.Load(assetDir, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<FeedMetadata?>(MetadataWith()));

        await BuildSweeper().Sweep(assetDir, Ct);

        Assert.False(Directory.Exists(orphanFlow));
    }

    [Fact]
    public async Task Sweep_DeletesStagingDir_EvenWhenManifestEntryExists()
    {
        var assetDir = AssetDir("BTCUSDT_Staging");
        var feedDir = Path.Combine(assetDir, "aggregated", "EqV_1m_1000");
        var stagingDir = Path.Combine(feedDir, ".staging-job-123");
        Directory.CreateDirectory(stagingDir);
        File.WriteAllText(Path.Combine(stagingDir, "2026-04.csv"), "ts,o,h,l,c,vol\n");
        File.WriteAllText(Path.Combine(feedDir, "2026-03.csv"), "ts,o,h,l,c,vol\n");

        _schema.Load(assetDir, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<FeedMetadata?>(MetadataWith("EqV_1m_1000")));

        await BuildSweeper().Sweep(assetDir, Ct);

        Assert.False(Directory.Exists(stagingDir));
        Assert.True(Directory.Exists(feedDir));
        Assert.True(File.Exists(Path.Combine(feedDir, "2026-03.csv")));
    }

    [Fact]
    public async Task Sweep_ManifestEntryWithoutDir_DoesNotMutateManifest()
    {
        var assetDir = AssetDir("BTCUSDT_Asymmetry");
        Directory.CreateDirectory(Path.Combine(assetDir, "aggregated"));

        _schema.Load(assetDir, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<FeedMetadata?>(MetadataWith("EqV_1m_2000")));

        await BuildSweeper().Sweep(assetDir, Ct);

        await _schema.Received().Load(assetDir, Arg.Any<CancellationToken>());
        await _schema.DidNotReceive().EnsureSchema(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string[]>(), Arg.Any<AutoApplySpec?>(), Arg.Any<CancellationToken>());
        await _schema.DidNotReceive().EnsureCandleConfig(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Sweep_NoAggregatedDir_IsNoOp()
    {
        var assetDir = AssetDir("BTCUSDT_NoAgg");

        await BuildSweeper().Sweep(assetDir, Ct);

        Assert.Empty(_logger.Entries);
    }

    [Fact]
    public async Task Sweep_AssetWithNoFeedsJson_ProcessesEverythingAsOrphan()
    {
        var assetDir = AssetDir("BTCUSDT_NoFeedsJson");
        var feedDir = Path.Combine(assetDir, "aggregated", "EqV_1m_1000");
        Directory.CreateDirectory(feedDir);
        File.WriteAllText(Path.Combine(feedDir, "2026-04.csv"), "ts,o,h,l,c,vol\n");

        _schema.Load(assetDir, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<FeedMetadata?>(null));

        await BuildSweeper().Sweep(assetDir, Ct);

        Assert.False(Directory.Exists(feedDir));
        Assert.Single(_logger.Warnings);
    }

    [Fact]
    public async Task Sweep_FeedDirHasBareAndPartNumberedCollision_LogsWarn_DoesNotDelete()
    {
        var assetDir = AssetDir("BTCUSDT_Collision");
        var feedDir = Path.Combine(assetDir, "aggregated", "EqV_1m_1000");
        Directory.CreateDirectory(feedDir);
        File.WriteAllText(Path.Combine(feedDir, "2026-05.csv"), "ts,o,h,l,c,vol\n");
        File.WriteAllText(Path.Combine(feedDir, "2026-05.p01.csv"), "ts,o,h,l,c,vol\n");

        _schema.Load(assetDir, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<FeedMetadata?>(MetadataWith("EqV_1m_1000")));

        await BuildSweeper().Sweep(assetDir, Ct);

        Assert.True(File.Exists(Path.Combine(feedDir, "2026-05.csv")));
        Assert.True(File.Exists(Path.Combine(feedDir, "2026-05.p01.csv")));

        Assert.Contains(_logger.Warnings, w =>
            w.Message.Contains("collision", StringComparison.OrdinalIgnoreCase) &&
            w.Message.Contains(Path.GetFullPath(feedDir), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Sweep_FeedDirHasOnlyPartNumberedFiles_DoesNotLogCollision()
    {
        var assetDir = AssetDir("BTCUSDT_NormalOverflow");
        var feedDir = Path.Combine(assetDir, "aggregated", "EqV_1m_1000");
        Directory.CreateDirectory(feedDir);
        File.WriteAllText(Path.Combine(feedDir, "2026-05.p01.csv"), "ts,o,h,l,c,vol\n");
        File.WriteAllText(Path.Combine(feedDir, "2026-05.p02.csv"), "ts,o,h,l,c,vol\n");

        _schema.Load(assetDir, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<FeedMetadata?>(MetadataWith("EqV_1m_1000")));

        await BuildSweeper().Sweep(assetDir, Ct);

        Assert.DoesNotContain(_logger.Warnings, w =>
            w.Message.Contains("collision", StringComparison.OrdinalIgnoreCase));
    }
}
