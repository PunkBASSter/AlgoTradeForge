using System.Globalization;
using System.Text;

namespace AlgoTradeForge.HistoryLoader.Application.Aggregation;

/// <summary>
/// Streaming CSV writer that emits monthly partition files under <c>feedDir</c> with size-
/// overflow handling: bare <c>YYYY-MM.csv</c> until <c>maxPartitionBytes</c> is exceeded, then
/// atomic-rename to <c>.p01.csv</c> and continue with <c>.p02.csv</c>. Once any month overflows,
/// every subsequent month pre-opens as <c>.p01.csv</c> (cross-month sticky). All writes go
/// through <c>*.tmp</c> and atomic-rename on close. Single-threaded — one writer per job.
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
            // Source reader guarantees chronological enumeration; a backward jump is a contract violation.
            if (string.CompareOrdinal(month, _openMonth) < 0)
            {
                throw new InvalidOperationException(
                    $"Out-of-order timestamp: incoming month '{month}' precedes open month '{_openMonth}'.");
            }
            FinalizeCurrentAsBareOrPart();
            OpenForMonth(month);
        }

        var rowBytes = Encoding.UTF8.GetBytes(csvRow + "\n");

        // Roll over BEFORE writing the over-budget row. Skip when the partition has only its
        // header — otherwise a single oversize row would loop forever.
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

    private void OpenForMonth(string month)
    {
        // Cross-month sticky: any prior overflow forces all subsequent months to pre-open at .p01.
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

        // Same-dir tmp + final → atomic rename guaranteed.
        SameVolumeGuard.Ensure(_tmpPath, _feedDir);

        _stream = new FileStream(_tmpPath, FileMode.Create, FileAccess.Write, FileShare.None);
        _stream.Write(_headerBytes);
        _bytesInCurrent = _headerBytes.Length;
    }

    private void HandleMidMonthOverflow()
    {
        // First overflow for the month: bare → p01, then open p02. Otherwise: pN → pN, open p(N+1).
        var month = _openMonth!;

        if (_openPartNumber is null)
        {
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
