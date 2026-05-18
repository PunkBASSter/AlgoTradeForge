using System.Globalization;
using System.Text;
using AlgoTradeForge.Application.IO;

namespace AlgoTradeForge.HistoryLoader.Application.Aggregation;

/// <summary>
/// Orchestrates the overwrite sequence for an aggregated feed via <see cref="IFileStorage"/>:
/// stage to <c>aggregated/&lt;feedId&gt;/.staging-&lt;jobId&gt;/</c>, then per-key
/// <see cref="IFileStorage.Move"/> each staged partition into the live slot, prune orphan
/// partitions, and <see cref="IFileStorage.DeleteByPrefix"/> the staging prefix. The local-FS
/// dir-rename-aside atomicity is gone — on S3 we get per-key atomicity instead. Crash-recovery
/// is delegated to <see cref="AggregatedDirSweeper"/>.
/// </summary>
public sealed class OverwritePathWriter
{
    private readonly IFileStorage _storage;

    public OverwritePathWriter(IFileStorage storage)
    {
        _storage = storage;
    }

    /// <summary>Computes the staging directory key for a job; clears any prior contents under that prefix.</summary>
    public async Task<string> PrepareStagingDir(string feedDir, string jobId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(jobId))
            throw new ArgumentException("jobId required.", nameof(jobId));

        var stagingDir = Path.Combine(feedDir, $".staging-{jobId}");
        await _storage.DeleteByPrefix(stagingDir, ct);
        return stagingDir;
    }

    /// <summary>
    /// Append-mode staging: copies pre-cutoff partitions verbatim and truncates the
    /// trailing-month file at <paramref name="priorLastBarTs"/> so the resume run re-emits
    /// the trailing bar.
    /// </summary>
    public async Task<AppendStagingPlan> PrepareStagingDirForAppend(
        string feedDir,
        string jobId,
        long priorLastBarTs,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(jobId))
            throw new ArgumentException("jobId required.", nameof(jobId));

        var stagingDir = Path.Combine(feedDir, $".staging-{jobId}");
        await _storage.DeleteByPrefix(stagingDir, ct);

        var trailingMonth = DateTimeOffset.FromUnixTimeMilliseconds(priorLastBarTs).UtcDateTime
            .ToString("yyyy-MM", CultureInfo.InvariantCulture);

        // List the live partitions only (top-level); staging is a sibling subdir we mustn't
        // recurse into.
        var allFiles = await ListLivePartitions(feedDir, ct);

        var hasEverOverflowed = allFiles
            .Select(f => PartitionFilenameParser.TryParse(Path.GetFileName(f), out _, out var part) && part is not null)
            .Any(b => b);

        var trailingFile = allFiles
            .Where(f =>
                PartitionFilenameParser.TryParse(Path.GetFileName(f), out var month, out _)
                && string.Equals(month, trailingMonth, StringComparison.Ordinal))
            .LastOrDefault();

        if (trailingFile is null)
            throw new InvalidOperationException(
                $"Append staging: trailing bar ts {priorLastBarTs} maps to month '{trailingMonth}' " +
                $"but no partition file matches that month under '{feedDir}'.");

        foreach (var src in allFiles)
        {
            if (string.Equals(src, trailingFile, StringComparison.Ordinal)) continue;
            var dest = Path.Combine(stagingDir, Path.GetFileName(src));
            await CopyKey(src, dest, ct);
        }

        var trailingFileName = Path.GetFileName(trailingFile);
        var truncatedDest = Path.Combine(stagingDir, trailingFileName);
        var truncatedBytes = await TruncateAtCutoff(trailingFile, truncatedDest, priorLastBarTs, ct);

        PartitionFilenameParser.TryParse(trailingFileName, out _, out var trailingPart);

        return new AppendStagingPlan(
            StagingDir: stagingDir,
            ResumeState: new PartitionAppendState(
                MonthKey: trailingMonth,
                PartNumber: trailingPart,
                FileBytes: truncatedBytes,
                HasEverOverflowed: hasEverOverflowed));
    }

    private async Task<List<string>> ListLivePartitions(string feedDir, CancellationToken ct)
    {
        var files = new List<string>();
        await foreach (var key in _storage.ListKeys(feedDir, suffix: ".csv", recursive: false, ct))
        {
            // Skip anything inside a nested staging dir — we asked recursive:false, but a future
            // backend might not honor that strictly.
            var rel = key.Substring(feedDir.Length).TrimStart('/', Path.DirectorySeparatorChar);
            if (rel.Contains('/') || rel.Contains(Path.DirectorySeparatorChar)) continue;
            files.Add(key);
        }
        files.Sort((a, b) => string.CompareOrdinal(Path.GetFileName(a), Path.GetFileName(b)));
        return files;
    }

    private async Task CopyKey(string srcKey, string dstKey, CancellationToken ct)
    {
        var bytes = await _storage.ReadAllBytes(srcKey, ct);
        await _storage.WriteAllBytes(dstKey, bytes, ct);
    }

    private async Task<long> TruncateAtCutoff(string srcKey, string dstKey, long cutoffTs, CancellationToken ct)
    {
        var session = await _storage.OpenWriteSession(dstKey, ct);
        var writer = new StreamWriter(session.Stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true)
        {
            NewLine = "\n",
        };

        long bytesWritten = 0;
        try
        {
            var firstLine = true;
            await foreach (var line in _storage.ReadLines(srcKey, ct))
            {
                if (firstLine)
                {
                    await writer.WriteLineAsync(line.AsMemory(), ct);
                    bytesWritten += Encoding.UTF8.GetByteCount(line) + 1;
                    firstLine = false;
                    continue;
                }
                if (line.Length == 0) continue;

                var commaIdx = line.IndexOf(',');
                if (commaIdx < 0) continue;
                if (!long.TryParse(
                        line.AsSpan(0, commaIdx),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out var ts))
                    continue;

                // Strict >= so the trailing bar itself is dropped (resume re-emits it).
                if (ts >= cutoffTs) break;

                await writer.WriteLineAsync(line.AsMemory(), ct);
                bytesWritten += Encoding.UTF8.GetByteCount(line) + 1;
            }
            await writer.FlushAsync(ct);
            // Drop the writer before committing — StreamWriter.DisposeAsync flushes the
            // underlying stream, which the session has already closed post-Commit. Explicit
            // ordering avoids the "Cannot access a closed file" race that scope-exit dispose
            // (in reverse declaration order) would otherwise trigger.
            await writer.DisposeAsync();
            await session.Commit(ct);
        }
        catch
        {
            await writer.DisposeAsync();
            await session.DisposeAsync();
            throw;
        }
        await session.DisposeAsync();
        return bytesWritten;
    }

    /// <summary>
    /// Promotes <paramref name="stagingDir"/> to be the new <paramref name="feedDir"/> via
    /// per-key <see cref="IFileStorage.Move"/>, pruning any live keys that the staging layout
    /// does not include. Not atomic across keys on S3; the sweeper reclaims partial state.
    /// </summary>
    public async Task Promote(string feedDir, string stagingDir, CancellationToken ct = default)
    {
        var stagingFiles = await ListStagingPartitions(stagingDir, ct);
        var liveFiles = await ListLivePartitions(feedDir, ct);
        var stagingNames = new HashSet<string>(
            stagingFiles.Select(Path.GetFileName)!,
            StringComparer.Ordinal);

        // Replace step: move each staging key into the live slot.
        foreach (var stagingKey in stagingFiles)
        {
            var name = Path.GetFileName(stagingKey);
            var liveKey = Path.Combine(feedDir, name);
            await _storage.Move(stagingKey, liveKey, overwrite: true, ct);
        }

        // Prune step: any pre-existing live partition whose name is NOT in the new staging set
        // is an orphan (e.g. the prior layout had `.p02.csv` that the new run no longer emits).
        foreach (var liveKey in liveFiles)
        {
            var name = Path.GetFileName(liveKey);
            if (!stagingNames.Contains(name))
                await _storage.Delete(liveKey, ct);
        }

        // Cleanup: drop the staging prefix in full (the moves above emptied it of partitions,
        // but any sidecar bookkeeping under the prefix is also discarded here).
        await _storage.DeleteByPrefix(stagingDir, ct);
    }

    private async Task<List<string>> ListStagingPartitions(string stagingDir, CancellationToken ct)
    {
        var files = new List<string>();
        await foreach (var key in _storage.ListKeys(stagingDir, suffix: ".csv", recursive: false, ct))
            files.Add(key);
        files.Sort((a, b) => string.CompareOrdinal(Path.GetFileName(a), Path.GetFileName(b)));
        return files;
    }

}

public sealed record AppendStagingPlan(
    string StagingDir,
    PartitionAppendState ResumeState);

public sealed record PartitionAppendState(
    string MonthKey,
    int? PartNumber,
    long FileBytes,
    bool HasEverOverflowed);
