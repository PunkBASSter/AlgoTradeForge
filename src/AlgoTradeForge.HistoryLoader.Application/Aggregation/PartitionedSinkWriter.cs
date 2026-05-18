using System.Globalization;
using System.Text;
using AlgoTradeForge.Application.IO;

namespace AlgoTradeForge.HistoryLoader.Application.Aggregation;

/// <summary>
/// Streaming CSV writer that emits monthly partition files under <c>feedDir</c> via
/// <see cref="IFileStorage.OpenWriteSession"/> with size-overflow handling: bare
/// <c>YYYY-MM.csv</c> until <c>maxPartitionBytes</c> is exceeded, then commit + atomic-rename
/// to <c>.p01.csv</c> and continue with <c>.p02.csv</c>. Once any month overflows, every
/// subsequent month pre-opens as <c>.p01.csv</c> (cross-month sticky) — this is what keeps
/// the speculative-bare commit-then-move state restricted to at most one event per job.
/// Single-threaded — one writer per job.
/// </summary>
public sealed class PartitionedSinkWriter : IAsyncDisposable
{
    private readonly IFileStorage _storage;
    private readonly string _feedDir;
    private readonly long _maxBytes;
    private readonly byte[] _headerBytes;

    private string? _openMonth;
    private int? _openPartNumber;
    private IObjectWriteSession? _session;
    private long _bytesInCurrent;
    private bool _hasEverOverflowed;
    private bool _disposed;

    private PartitionedSinkWriter(IFileStorage storage, string feedDir, long maxPartitionBytes, string headerLine)
    {
        if (maxPartitionBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxPartitionBytes), "Must be positive.");
        _storage = storage;
        _feedDir = feedDir;
        _maxBytes = maxPartitionBytes;
        _headerBytes = Encoding.UTF8.GetBytes(headerLine + "\n");
    }

    public static Task<PartitionedSinkWriter> Open(
        IFileStorage storage,
        string feedDir,
        long maxPartitionBytes,
        string headerLine,
        CancellationToken ct = default)
        => Open(storage, feedDir, maxPartitionBytes, headerLine, resumeState: null, ct);

    /// <summary>
    /// Open the writer. When <paramref name="resumeState"/> is non-null, takes ownership of the
    /// pre-staged trailing partition (re-opens its key via <see cref="IFileStorage.OpenWriteSession"/>
    /// after seeding the header + existing rows so subsequent rows append correctly).
    /// </summary>
    public static async Task<PartitionedSinkWriter> Open(
        IFileStorage storage,
        string feedDir,
        long maxPartitionBytes,
        string headerLine,
        PartitionAppendState? resumeState,
        CancellationToken ct = default)
    {
        var writer = new PartitionedSinkWriter(storage, feedDir, maxPartitionBytes, headerLine);
        if (resumeState is not null)
            await writer.OpenForAppend(resumeState, ct);
        return writer;
    }

    private async Task OpenForAppend(PartitionAppendState state, CancellationToken ct)
    {
        var finalName = NameFor(state.MonthKey, state.PartNumber);
        var finalKey = Path.Combine(_feedDir, finalName);
        if (!await _storage.Exists(finalKey, ct))
            throw new InvalidOperationException(
                $"Append-mode resume requires pre-staged file '{finalName}' under '{_feedDir}'.");

        // Read the existing rows, open a new session at the same key, replay them into it,
        // then continue appending. Object-store sessions are write-only — there's no "open
        // for append" verb — so the truncate-on-resume cycle is read-merge-write.
        var existingBytes = await _storage.ReadAllBytes(finalKey, ct);

        _session = await _storage.OpenWriteSession(finalKey, ct);
        await _session.Stream.WriteAsync(existingBytes, ct);

        _openMonth = state.MonthKey;
        _openPartNumber = state.PartNumber;
        _bytesInCurrent = state.FileBytes;
        _hasEverOverflowed = state.HasEverOverflowed;
    }

    public async Task WriteRow(long tsEpochMs, string csvRow, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var month = DateTimeOffset.FromUnixTimeMilliseconds(tsEpochMs).UtcDateTime
            .ToString("yyyy-MM", CultureInfo.InvariantCulture);

        if (_openMonth is null)
        {
            await OpenForMonth(month, ct);
        }
        else if (!string.Equals(month, _openMonth, StringComparison.Ordinal))
        {
            // Source reader guarantees chronological enumeration; a backward jump is a contract violation.
            if (string.CompareOrdinal(month, _openMonth) < 0)
            {
                throw new InvalidOperationException(
                    $"Out-of-order timestamp: incoming month '{month}' precedes open month '{_openMonth}'.");
            }
            await CommitCurrent(ct);
            await OpenForMonth(month, ct);
        }

        var rowBytes = Encoding.UTF8.GetBytes(csvRow + "\n");

        // Roll over BEFORE writing the over-budget row. Skip when the partition has only its
        // header — otherwise a single oversize row would loop forever.
        if (_bytesInCurrent + rowBytes.Length > _maxBytes && _bytesInCurrent > _headerBytes.Length)
        {
            await HandleMidMonthOverflow(ct);
        }

        await _session!.Stream.WriteAsync(rowBytes, ct);
        _bytesInCurrent += rowBytes.Length;
    }

    /// <summary>
    /// Commits the in-flight partition session and transitions the writer to a terminal state.
    /// Callers MUST invoke this on the success path before <see cref="DisposeAsync"/>; the
    /// dispose path aborts any uncommitted session so cancellation can't publish partial data.
    /// </summary>
    public async Task Complete(CancellationToken ct = default)
    {
        if (_disposed) return;
        if (_session is not null)
            await CommitCurrent(ct);
        _disposed = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        if (_session is not null)
        {
            // No Complete was called — treat as abort. Disposing the session without commit
            // routes through LocalWriteSession.Abort, which deletes the .tmp without publishing.
            await _session.DisposeAsync();
            _session = null;
        }
        _disposed = true;
    }

    private async Task OpenForMonth(string month, CancellationToken ct)
    {
        // Cross-month sticky: any prior overflow forces all subsequent months to pre-open at .p01.
        if (_hasEverOverflowed)
            await OpenAt(month, partNumber: 1, ct);
        else
            await OpenAt(month, partNumber: null, ct);
    }

    private async Task OpenAt(string month, int? partNumber, CancellationToken ct)
    {
        _openMonth = month;
        _openPartNumber = partNumber;
        var finalName = NameFor(month, partNumber);
        var finalKey = Path.Combine(_feedDir, finalName);

        _session = await _storage.OpenWriteSession(finalKey, ct);
        await _session.Stream.WriteAsync(_headerBytes, ct);
        _bytesInCurrent = _headerBytes.Length;
    }

    private async Task HandleMidMonthOverflow(CancellationToken ct)
    {
        var month = _openMonth!;

        if (_openPartNumber is null)
        {
            // First overflow ever — current bare session must publish as `<month>.p01.csv`.
            // OpenWriteSession is bound to the bare key; commit publishes the bare file, then
            // Move retargets it to `.p01`. The transient bare file lives briefly inside the
            // private staging dir, so no observer can see the inconsistent state.
            var bareKey = Path.Combine(_feedDir, NameFor(month, null));
            var p01Key = Path.Combine(_feedDir, NameFor(month, 1));
            await CommitCurrent(ct);
            await _storage.Move(bareKey, p01Key, overwrite: false, ct);
            _hasEverOverflowed = true;
            await OpenAt(month, partNumber: 2, ct);
        }
        else
        {
            // Subsequent overflow: session is already targeting the correct `.pN` key.
            await CommitCurrent(ct);
            await OpenAt(month, partNumber: _openPartNumber + 1, ct);
        }
    }

    private async Task CommitCurrent(CancellationToken ct)
    {
        if (_session is null) return;
        await _session.Commit(ct);
        await _session.DisposeAsync();
        _session = null;
        _openMonth = null;
        _openPartNumber = null;
        _bytesInCurrent = 0;
    }

    private static string NameFor(string month, int? partNumber) =>
        partNumber is null
            ? $"{month}.csv"
            : $"{month}.p{partNumber.Value:D2}.csv";
}
