using AlgoTradeForge.HistoryLoader.Application.Aggregation;
using AlgoTradeForge.Infrastructure.IO;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Storage;

/// <summary>
/// <see cref="OverwritePathWriter"/> happy-path tests over <see cref="LocalFileStorage"/>.
/// Cross-volume guard semantics are gone — move atomicity is owned by IFileStorage now.
/// </summary>
public sealed class OverwritePathWriterTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), $"OverwritePathWriterTests_{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task PrepareStagingDir_SameVolume_CreatesDirectory()
    {
        var writer = new OverwritePathWriter(new LocalFileStorage());

        var feedDir = Path.Combine(_tempDir, "EqV_same");
        Directory.CreateDirectory(feedDir);

        var staging = await writer.PrepareStagingDir(feedDir, "job-456", Ct);

        Assert.Equal(feedDir, Path.GetDirectoryName(staging));
        Assert.Equal(".staging-job-456", Path.GetFileName(staging));
    }

    [Fact]
    public async Task Promote_ReplacesLiveContentsWithStagedContents()
    {
        var writer = new OverwritePathWriter(new LocalFileStorage());
        var feedDir = Path.Combine(_tempDir, "EqV_promote");
        Directory.CreateDirectory(feedDir);

        // Plant pre-existing live content.
        File.WriteAllText(Path.Combine(feedDir, "2026-03.csv"), "OLD-MAR\n");
        File.WriteAllText(Path.Combine(feedDir, "2026-04.csv"), "OLD-APR\n");

        // Stage new content.
        var stagingDir = await writer.PrepareStagingDir(feedDir, "job-promote-1", Ct);
        Directory.CreateDirectory(stagingDir);
        File.WriteAllText(Path.Combine(stagingDir, "2026-03.csv"), "NEW-MAR\n");
        File.WriteAllText(Path.Combine(stagingDir, "2026-05.csv"), "NEW-MAY\n");

        await writer.Promote(feedDir, stagingDir, Ct);

        // After Promote: feedDir contains ONLY the staged contents.
        var files = Directory.EnumerateFiles(feedDir).Select(Path.GetFileName).OrderBy(n => n).ToList();
        Assert.Equal(["2026-03.csv", "2026-05.csv"], files);
        Assert.Equal("NEW-MAR\n", File.ReadAllText(Path.Combine(feedDir, "2026-03.csv")));
        Assert.Equal("NEW-MAY\n", File.ReadAllText(Path.Combine(feedDir, "2026-05.csv")));

        // Staging dir is gone (consumed by Promote).
        Assert.False(Directory.Exists(stagingDir));
    }
}
