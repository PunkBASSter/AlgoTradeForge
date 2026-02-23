using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AlgoTradeForge.Domain.Events;

namespace AlgoTradeForge.Application.Events;

public sealed record RunIdentity
{
    public required string StrategyName { get; init; }
    public string StrategyVersion { get; init; } = "0";
    public required string AssetName { get; init; }
    public required DateTimeOffset StartTime { get; init; }
    public required DateTimeOffset EndTime { get; init; }
    public required long InitialCash { get; init; }
    public required ExportMode RunMode { get; init; }
    public required DateTimeOffset RunTimestamp { get; init; }
    public IDictionary<string, object>? StrategyParameters { get; init; }

    /// <summary>
    /// Computes the run folder name:
    /// {strategy}_v{version}_{asset}_{startYear}-{endYear}_{hash}_{yyyyMMddTHHmmssfff}
    /// </summary>
    public string ComputeFolderName()
    {
        var hash = ComputeParamsHash(StrategyParameters);
        var ts = RunTimestamp.UtcDateTime.ToString("yyyyMMdd'T'HHmmssfff");
        return $"{StrategyName}_v{StrategyVersion}_{AssetName}_{StartTime.Year}-{EndTime.Year}_{hash}_{ts}";
    }

    /// <summary>
    /// Sorted JSON → SHA256 → first 3 bytes → 6 hex chars.
    /// Null or empty → "000000".
    /// </summary>
    internal static string ComputeParamsHash(IDictionary<string, object>? parameters)
    {
        if (parameters is null || parameters.Count == 0)
            return "000000";

        var sorted = new SortedDictionary<string, object>(parameters);
        var json = JsonSerializer.Serialize(sorted);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(bytes, 0, 3).ToLowerInvariant();
    }
}
