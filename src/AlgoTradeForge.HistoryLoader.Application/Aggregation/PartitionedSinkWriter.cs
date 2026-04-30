using System.Globalization;
using System.Text;

namespace AlgoTradeForge.HistoryLoader.Application.Aggregation;

/// <summary>
/// Streaming CSV writer that emits monthly partition files under <paramref name="feedDir"/>
/// with the size-overflow scheme from TRD §3.2:
/// <list type="bullet">
///   <item>Default partition name <c>&lt;YYYY&gt;-&lt;MM&gt;.csv</c>.</item>
///   <item>When bytes-written exceeds <c>maxPartitionBytes</c> mid-month and the partition
///         is bare-named, the in-progress file atomic-renames to <c>&lt;YYYY&gt;-&lt;MM&gt;.p01.csv</c>
///         and writing continues in <c>&lt;YYYY&gt;-&lt;MM&gt;.p02.csv</c>. Sticky for the
///         rest of the month — a month is either single-file or part-numbered, never mixed.</item>
///   <item>Once any month overflows, all subsequent months pre-open as
///         <c>&lt;YYYY&gt;-&lt;MM&gt;.p01.csv</c> from the first bar (cross-month sticky).</item>
///   <item>Every partition write goes to <c>&lt;name&gt;.tmp</c> and atomic-renames on close.</item>
/// </list>
/// Standalone in Phase 1a (test-fed); aggregator-fed in Phase 1b. Single-threaded by design —
/// the aggregator owns one writer per job and feeds it serially.
/// </summary>
public sealed class PartitionedSinkWriter : IDisposable
{
    private readonly string _feedDir;
    private readonly long _maxBytes;
    private readonly byte[] _headerBytes;

    private string? _openMonth;        // "YYYY-MM" of the currently-open partition
    private int? _openPartNumber;      // null = bare <month>.csv; >=1 = .pNN
    private FileStream? _stream;
    private string? _tmpPath;
    private long _bytesInCurrent;
    private bool _hasEverOverflowed;
    private bool _disposed;

    public PartitionedSinkWriter(string feedDir, long maxPartitionBytes, string headerLine)
    {
        if (maxPartitionBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxPartitionBytes),
                "Must be positive.");
        _feedDir = feedDir;
        _maxBytes = maxPartitionBytes;
        _headerBytes = Encoding.UTF8.GetBytes(headerLine + "\n");
        Directory.CreateDirectory(_feedDir);
    }

    public void WriteRow(long tsEpochMs, string csvRow)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var month = DateTimeOffset.FromUnixTimeMilliseconds(tsEpochMs).UtcDateTime
            .ToString("yyyy-MM", CultureInfo.InvariantCulture);

        if (_openMonth is null)
        {
            OpenForMonth(month);
        }
        else if (!string.Equals(month, _openMonth, StringComparison.Ordinal))
        {
            // Source reader (TRD §6.2) guarantees chronological enumeration. A backward
            // jump is a contract violation; surface loudly.
            if (string.CompareOrdinal(month, _openMonth) < 0)
            {
                throw new InvalidOperationException(
                    $"Out-of-order timestamp: incoming month '{month}' precedes open month '{_openMonth}'.");
            }
            FinalizeCurrentAsBareOrPart();
            OpenForMonth(month);
        }

        var rowBytes = Encoding.UTF8.GetBytes(csvRow + "\n");

        // Roll over BEFORE writing the row that would exceed the budget. Skip the rollover
        // when the current partition is empty-of-data (only header written) — otherwise a
        // single oversize row would loop forever.
        if (_bytesInCurrent + rowBytes.Length > _maxBytes && _bytesInCurrent > _headerBytes.Length)
        {
            HandleMidMonthOverflow();
        }

        _stream!.Write(rowBytes);
        _bytesInCurrent += rowBytes.Length;
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (_stream is not null)
            FinalizeCurrentAsBareOrPart();
        _disposed = true;
    }

    // -------------------------------------------------------------------------

    private void OpenForMonth(string month)
    {
        // Cross-month sticky: any prior overflow in THIS writer's lifetime forces every
        // subsequent month to pre-open at .p01.
        if (_hasEverOverflowed)
            OpenAt(month, partNumber: 1);
        else
            OpenAt(month, partNumber: null);
    }

    private void OpenAt(string month, int? partNumber)
    {
        _openMonth = month;
        _openPartNumber = partNumber;
        var finalName = NameFor(month, partNumber);
        _tmpPath = Path.Combine(_feedDir, finalName + ".tmp");

        // Co-locate tmp + final in the same dir → guaranteed same-volume rename.
        SameVolumeGuard.Ensure(_tmpPath, _feedDir);

        _stream = new FileStream(_tmpPath, FileMode.Create, FileAccess.Write, FileShare.None);
        _stream.Write(_headerBytes);
        _bytesInCurrent = _headerBytes.Length;
    }

    private void HandleMidMonthOverflow()
    {
        // The current partition will be finalized — but as which name? If we were writing the
        // bare <month>.csv.tmp, this is the FIRST overflow for the month: rename to
        // <month>.p01.csv and open .p02.csv. Otherwise (already part-numbered): rename to
        // <month>.p<N>.csv and open p<N+1>.csv.
        var month = _openMonth!;

        if (_openPartNumber is null)
        {
            // Promote bare → p01.
            CloseStream();
            File.Move(_tmpPath!, Path.Combine(_feedDir, NameFor(month, 1)), overwrite: false);
            _tmpPath = null;
            _hasEverOverflowed = true;
            OpenAt(month, partNumber: 2);
        }
        else
        {
            CloseStream();
            File.Move(_tmpPath!, Path.Combine(_feedDir, NameFor(month, _openPartNumber.Value)), overwrite: false);
            _tmpPath = null;
            OpenAt(month, partNumber: _openPartNumber + 1);
        }
    }

    private void FinalizeCurrentAsBareOrPart()
    {
        if (_stream is null) return;

        CloseStream();
        File.Move(_tmpPath!, Path.Combine(_feedDir, NameFor(_openMonth!, _openPartNumber)), overwrite: false);
        _tmpPath = null;
        _openMonth = null;
        _openPartNumber = null;
        _bytesInCurrent = 0;
    }

    private void CloseStream()
    {
        _stream?.Flush();
        _stream?.Dispose();
        _stream = null;
    }

    private static string NameFor(string month, int? partNumber) =>
        partNumber is null
            ? $"{month}.csv"
            : $"{month}.p{partNumber.Value:D2}.csv";
}
