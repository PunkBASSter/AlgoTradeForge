using AlgoTradeForge.HistoryLoader.Application.Aggregation;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Storage;

/// <summary>
/// P1a-11, P1a-12 — <see cref="SameVolumeGuard"/> and <see cref="OverwritePathWriter"/>
/// reject cross-volume staging configurations.
/// </summary>
public sealed class CrossVolumeGuardTests : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), $"CrossVolumeGuardTests_{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // -------------------------------------------------------------------------
    // SameVolumeGuard direct tests (custom resolver — works on single-drive CI)
    // -------------------------------------------------------------------------

    [Fact]
    public void Ensure_SameVolume_DoesNotThrow()
    {
        SameVolumeGuard.VolumeResolver fixedRoot = _ => @"C:\";
        SameVolumeGuard.Ensure(@"C:\foo\bar", @"C:\baz", fixedRoot);
        // No exception.
    }

    [Fact]
    public void Ensure_DifferentVolumes_Throws()
    {
        SameVolumeGuard.VolumeResolver perPath = path =>
            path.StartsWith(@"D:\", StringComparison.Ordinal) ? @"D:\" : @"C:\";

        var ex = Assert.Throws<InvalidOperationException>(
            () => SameVolumeGuard.Ensure(@"D:\stage", @"C:\target", perPath));

        Assert.Contains("same volume", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("D:\\", ex.Message, StringComparison.Ordinal);
        Assert.Contains("C:\\", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Ensure_VolumeComparisonIsCaseInsensitive()
    {
        SameVolumeGuard.VolumeResolver mixedCase = path =>
            path == "lower" ? @"c:\" : @"C:\";

        SameVolumeGuard.Ensure("lower", "upper", mixedCase);
        // No exception — Windows volume letters compare case-insensitively.
    }

    // -------------------------------------------------------------------------
    // OverwritePathWriter — staging dir on a different volume rejected
    // -------------------------------------------------------------------------

    [Fact]
    public void PrepareStagingDir_CrossVolume_Throws()
    {
        // Inject a resolver that lies: feedDir is on D:, but the computed staging path
        // (a child of feedDir) is reported as C: — simulating a junction or symlink that
        // crosses volumes. The guard catches this even though the strings look related.
        SameVolumeGuard.VolumeResolver fakeCrossVolume = path =>
            path.Contains(".staging-", StringComparison.Ordinal) ? @"C:\" : @"D:\";

        var writer = new OverwritePathWriter(fakeCrossVolume);

        var feedDir = Path.Combine(_tempDir, "EqV_cross");
        Directory.CreateDirectory(feedDir);

        var ex = Assert.Throws<InvalidOperationException>(
            () => writer.PrepareStagingDir(feedDir, "job-123"));

        Assert.Contains("same volume", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PrepareStagingDir_SameVolume_CreatesDirectory()
    {
        var writer = new OverwritePathWriter();

        var feedDir = Path.Combine(_tempDir, "EqV_same");
        Directory.CreateDirectory(feedDir);

        var staging = writer.PrepareStagingDir(feedDir, "job-456");

        Assert.True(Directory.Exists(staging));
        Assert.Equal(feedDir, Path.GetDirectoryName(staging));
        Assert.Equal(".staging-job-456", Path.GetFileName(staging));
    }

    // -------------------------------------------------------------------------
    // OverwritePathWriter end-to-end happy path (single volume)
    // -------------------------------------------------------------------------

    [Fact]
    public void Promote_ReplacesLiveContentsWithStagedContents()
    {
        var writer = new OverwritePathWriter();
        var feedDir = Path.Combine(_tempDir, "EqV_promote");
        Directory.CreateDirectory(feedDir);

        // Plant pre-existing live content.
        File.WriteAllText(Path.Combine(feedDir, "2026-03.csv"), "OLD-MAR\n");
        File.WriteAllText(Path.Combine(feedDir, "2026-04.csv"), "OLD-APR\n");

        // Stage new content.
        var stagingDir = writer.PrepareStagingDir(feedDir, "job-promote-1");
        File.WriteAllText(Path.Combine(stagingDir, "2026-03.csv"), "NEW-MAR\n");
        File.WriteAllText(Path.Combine(stagingDir, "2026-05.csv"), "NEW-MAY\n");

        writer.Promote(feedDir, stagingDir);

        // After Promote: feedDir contains ONLY the staged contents.
        var files = Directory.EnumerateFiles(feedDir).Select(Path.GetFileName).OrderBy(n => n).ToList();
        Assert.Equal(["2026-03.csv", "2026-05.csv"], files);
        Assert.Equal("NEW-MAR\n", File.ReadAllText(Path.Combine(feedDir, "2026-03.csv")));
        Assert.Equal("NEW-MAY\n", File.ReadAllText(Path.Combine(feedDir, "2026-05.csv")));

        // Staging dir is gone (consumed by Promote).
        Assert.False(Directory.Exists(stagingDir));

        // Renamed-aside .deleted-* dir is also cleaned (best-effort; if leftover, sweep handles it).
        var leftovers = Directory.EnumerateDirectories(_tempDir, "*.deleted-*").ToList();
        Assert.Empty(leftovers);
    }
}
