namespace AlgoTradeForge.Storage;

/// <summary>
/// Seeks the 8 KiB tail of a partition file and returns its last non-empty line. Bounded
/// window keeps it cheap on large partitions; rows longer than 8 KiB return null.
/// </summary>
public sealed class LocalTailIndex : IPartitionTailIndex
{
    private const int TailWindowBytes = 8 * 1024;

    private readonly IFileStorage _storage;

    public LocalTailIndex(IFileStorage storage)
    {
        _storage = storage;
    }

    public async Task<string?> GetLastLine(string key, CancellationToken ct = default)
    {
        if (!await _storage.Exists(key, ct)) return null;
        await using var stream = await _storage.OpenRead(key, ct);
        if (!stream.CanSeek) return null;
        if (stream.Length == 0) return null;

        var bufLen = (int)Math.Min(TailWindowBytes, stream.Length);
        stream.Seek(-bufLen, SeekOrigin.End);
        var buf = new byte[bufLen];
        var read = await stream.ReadAsync(buf.AsMemory(0, bufLen), ct);

        return TailExtractor.ExtractLastLine(buf, read);
    }
}
