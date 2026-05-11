using Xunit;

namespace AlgoTradeForge.Infrastructure.Tests.History;

/// <summary>
/// P1a-32: Pin the legacy loader's removal so a future refactor doesn't accidentally
/// reintroduce <c>CsvInt64BarLoader</c> alongside the new <c>PartitionedCsvBarLoader</c>.
/// </summary>
public sealed class LegacyLoaderRemovedTest
{
    [Fact]
    public void CsvInt64BarLoader_TypeIsNotResolvable()
    {
        var legacyType = Type.GetType(
            "AlgoTradeForge.Infrastructure.CandleIngestion.CsvInt64BarLoader, AlgoTradeForge.Infrastructure",
            throwOnError: false);

        Assert.Null(legacyType);
    }

    [Fact]
    public void CsvInt64BarLoaderTests_TypeIsNotResolvable()
    {
        var legacyTestType = Type.GetType(
            "AlgoTradeForge.Infrastructure.Tests.CandleIngestion.CsvInt64BarLoaderTests, AlgoTradeForge.Infrastructure.Tests",
            throwOnError: false);

        Assert.Null(legacyTestType);
    }
}
