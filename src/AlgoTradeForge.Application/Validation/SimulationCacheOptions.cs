namespace AlgoTradeForge.Application.Validation;

/// <summary>
/// Configuration for simulation cache storage behavior.
/// When the estimated cache size exceeds <see cref="SpilloverThresholdBytes"/>,
/// the cache is written to a binary file and accessed via memory-mapped I/O.
/// </summary>
public sealed record SimulationCacheOptions
{
    /// <summary>Cache size threshold (in bytes) above which spillover to disk occurs. Default: 200 MB.</summary>
    public long SpilloverThresholdBytes { get; init; } = 200L * 1024 * 1024;

    /// <summary>Directory for cache files. Defaults to {LocalAppData}/AlgoTradeForge/Cache.</summary>
    public string CacheDirectory { get; init; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AlgoTradeForge", "Cache");
}
