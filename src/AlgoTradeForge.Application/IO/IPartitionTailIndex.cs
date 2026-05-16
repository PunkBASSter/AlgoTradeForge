namespace AlgoTradeForge.Application.IO;

/// <summary>
/// Cheap last-timestamp lookup for partition restart. Local impl reads trailing bytes;
/// S3 impl reads a sidecar object updated on each flush.
/// </summary>
public interface IPartitionTailIndex
{
    /// <summary>
    /// Assumes the timestamp is the first comma-separated <see cref="long"/> on the last
    /// non-empty line. Returns null if the key is missing, empty, or unparseable.
    /// </summary>
    Task<long?> GetLastTimestamp(string key, CancellationToken ct = default);
}
