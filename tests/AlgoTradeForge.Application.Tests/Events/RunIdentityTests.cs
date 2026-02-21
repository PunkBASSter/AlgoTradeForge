using AlgoTradeForge.Application.Events;
using AlgoTradeForge.Domain.Events;
using Xunit;

namespace AlgoTradeForge.Application.Tests.Events;

public class RunIdentityTests
{
    private static RunIdentity MakeIdentity(
        string strategy = "SmaStrategy",
        string version = "0",
        string asset = "BTCUSDT",
        DateTimeOffset? start = null,
        DateTimeOffset? end = null,
        long initialCash = 100_000,
        ExportMode runMode = ExportMode.Backtest,
        DateTimeOffset? runTimestamp = null,
        IDictionary<string, object>? parameters = null)
    {
        return new RunIdentity
        {
            StrategyName = strategy,
            StrategyVersion = version,
            AssetName = asset,
            StartTime = start ?? new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
            EndTime = end ?? new DateTimeOffset(2024, 12, 31, 0, 0, 0, TimeSpan.Zero),
            InitialCash = initialCash,
            RunMode = runMode,
            RunTimestamp = runTimestamp ?? new DateTimeOffset(2025, 6, 15, 14, 30, 0, TimeSpan.Zero),
            StrategyParameters = parameters,
        };
    }

    [Fact]
    public void ComputeFolderName_MatchesExpectedFormat()
    {
        var id = MakeIdentity();

        var folder = id.ComputeFolderName();

        // Pattern: {strategy}_v{version}_{asset}_{startYear}-{endYear}_{hash}_{yyyyMMddTHHmmss}
        Assert.StartsWith("SmaStrategy_v0_BTCUSDT_2024-2024_", folder);
        Assert.EndsWith("_20250615T143000", folder);
        // hash is 6 hex chars for null params
        Assert.Contains("_000000_", folder);
    }

    [Fact]
    public void ComputeFolderName_DifferentYears_ShowsBothYears()
    {
        var id = MakeIdentity(
            start: new DateTimeOffset(2022, 3, 1, 0, 0, 0, TimeSpan.Zero),
            end: new DateTimeOffset(2024, 6, 15, 0, 0, 0, TimeSpan.Zero));

        var folder = id.ComputeFolderName();

        Assert.Contains("_2022-2024_", folder);
    }

    [Fact]
    public void ComputeParamsHash_NullParams_Returns000000()
    {
        var hash = RunIdentity.ComputeParamsHash(null);

        Assert.Equal("000000", hash);
    }

    [Fact]
    public void ComputeParamsHash_EmptyDict_Returns000000()
    {
        var hash = RunIdentity.ComputeParamsHash(new Dictionary<string, object>());

        Assert.Equal("000000", hash);
    }

    [Fact]
    public void ComputeParamsHash_Deterministic()
    {
        var p1 = new Dictionary<string, object> { ["fast"] = 10, ["slow"] = 30 };
        var p2 = new Dictionary<string, object> { ["fast"] = 10, ["slow"] = 30 };

        Assert.Equal(RunIdentity.ComputeParamsHash(p1), RunIdentity.ComputeParamsHash(p2));
    }

    [Fact]
    public void ComputeParamsHash_OrderIndependent()
    {
        var p1 = new Dictionary<string, object> { ["fast"] = 10, ["slow"] = 30 };
        var p2 = new Dictionary<string, object> { ["slow"] = 30, ["fast"] = 10 };

        Assert.Equal(RunIdentity.ComputeParamsHash(p1), RunIdentity.ComputeParamsHash(p2));
    }

    [Fact]
    public void ComputeParamsHash_DifferentValues_DifferentHash()
    {
        var p1 = new Dictionary<string, object> { ["fast"] = 10 };
        var p2 = new Dictionary<string, object> { ["fast"] = 20 };

        Assert.NotEqual(RunIdentity.ComputeParamsHash(p1), RunIdentity.ComputeParamsHash(p2));
    }

    [Fact]
    public void ComputeParamsHash_Returns6HexChars()
    {
        var p = new Dictionary<string, object> { ["period"] = 14 };

        var hash = RunIdentity.ComputeParamsHash(p);

        Assert.Equal(6, hash.Length);
        Assert.Matches("^[0-9a-f]{6}$", hash);
    }

    [Fact]
    public void ComputeFolderName_WithParams_IncludesHash()
    {
        var id = MakeIdentity(parameters: new Dictionary<string, object> { ["period"] = 14 });

        var folder = id.ComputeFolderName();

        // Should NOT contain 000000 when params are provided
        Assert.DoesNotContain("_000000_", folder);
    }
}
