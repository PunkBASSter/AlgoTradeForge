using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Application.Archive;
using AlgoTradeForge.HistoryLoader.Domain;
using AlgoTradeForge.HistoryLoader.WebApi.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Archive;

public sealed class CoverageEndpointTests
{
    private static (IOptionsMonitor<HistoryLoaderOptions>, ISchemaManager, IFeedStatusStore, IMonthCoverageCalculator) BuildDeps()
    {
        var options = Substitute.For<IOptionsMonitor<HistoryLoaderOptions>>();
        options.CurrentValue.Returns(new HistoryLoaderOptions());
        return (options, Substitute.For<ISchemaManager>(), Substitute.For<IFeedStatusStore>(), Substitute.For<IMonthCoverageCalculator>());
    }

    [Fact]
    public async Task UnknownAssetType_Returns422_NotException()
    {
        var (options, schema, status, coverage) = BuildDeps();

        var result = await CoverageEndpoints.GetCoverage(
            "binance", "BTCUSDT", "alien",
            options, schema, status, coverage,
            TestContext.Current.CancellationToken);

        var statusResult = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, statusResult.StatusCode);
    }

    [Theory]
    [InlineData("../evil", "BTCUSDT")]
    [InlineData("bin\\..\\..", "BTCUSDT")]
    [InlineData("binance", "../secrets")]
    [InlineData("binance", "BTC/USDT")]
    public async Task TraversalInExchangeOrSymbol_Returns422_NoFilesystemTouch(string exchange, string symbol)
    {
        var (options, schema, status, coverage) = BuildDeps();

        var result = await CoverageEndpoints.GetCoverage(
            exchange, symbol, AssetTypes.Spot,
            options, schema, status, coverage,
            TestContext.Current.CancellationToken);

        var statusResult = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, statusResult.StatusCode);
        // Schema must never be queried (no filesystem touch).
        await schema.DidNotReceiveWithAnyArgs().Load(default!, default!);
    }
}
