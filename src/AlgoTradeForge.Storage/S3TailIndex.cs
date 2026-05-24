namespace AlgoTradeForge.Storage;

/// <summary>
/// S3 counterpart to <see cref="LocalTailIndex"/>: issues a single Range GET for the last 8 KiB
/// of the partition object and returns the last non-empty line. The plan doc originally proposed
/// a <c>{key}.tail</c> sidecar; range-GET is simpler (no per-flush PUT amplification, no extra
/// keys to manage) and the tail is only consulted at resume — not on every flush — so paying
/// one network round-trip per partition open is fine.
/// </summary>
public sealed class S3TailIndex : IPartitionTailIndex
{
    private const int TailWindowBytes = 8 * 1024;

    private readonly S3FileStorage _storage;

    public S3TailIndex(S3FileStorage storage)
    {
        _storage = storage;
    }

    public async Task<string?> GetLastLine(string key, CancellationToken ct = default)
    {
        var buf = await _storage.GetTail(key, TailWindowBytes, ct);
        if (buf is null) return null;
        return TailExtractor.ExtractLastLine(buf, buf.Length);
    }
}
