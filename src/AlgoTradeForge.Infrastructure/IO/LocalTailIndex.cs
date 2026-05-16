using System.Globalization;
using AlgoTradeForge.Application.IO;

namespace AlgoTradeForge.Infrastructure.IO;

/// <summary>
/// Seeks the 8 KiB tail of a partition file and parses the first comma field of its last
/// non-empty line as a <see cref="long"/> timestamp. Bounded window keeps it cheap on
/// large partitions; rows longer than 8 KiB return null.
/// </summary>
public sealed class LocalTailIndex : IPartitionTailIndex
{
    private const int TailWindowBytes = 8 * 1024;

    private readonly IFileStorage _storage;

    public LocalTailIndex(IFileStorage storage)
    {
        _storage = storage;
    }

    public async Task<long?> GetLastTimestamp(string key, CancellationToken ct = default)
    {
        if (!await _storage.Exists(key, ct)) return null;
        await using var stream = await _storage.OpenRead(key, ct);
        if (!stream.CanSeek) return null;
        if (stream.Length == 0) return null;

        var bufLen = (int)Math.Min(TailWindowBytes, stream.Length);
        stream.Seek(-bufLen, SeekOrigin.End);
        var buf = new byte[bufLen];
        var read = await stream.ReadAsync(buf.AsMemory(0, bufLen), ct);

        // Walk backward from EOF, skipping trailing newlines, to find the start of the
        // last non-empty line. If the file has only a header or no newline at all, return null.
        var end = read;
        while (end > 0 && (buf[end - 1] == (byte)'\n' || buf[end - 1] == (byte)'\r')) end--;
        if (end == 0) return null;

        var start = end - 1;
        while (start > 0 && buf[start - 1] != (byte)'\n' && buf[start - 1] != (byte)'\r') start--;

        var lastLine = System.Text.Encoding.UTF8.GetString(buf, start, end - start);
        var comma = lastLine.IndexOf(',');
        var field = comma < 0 ? lastLine : lastLine.AsSpan(0, comma).ToString();
        return long.TryParse(field, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ts) ? ts : null;
    }
}
