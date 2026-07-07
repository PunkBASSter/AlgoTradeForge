using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Application.Archive;
using AlgoTradeForge.HistoryLoader.WebApi.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Archive;

public sealed class CoverageEndpointTests
{
    [Fact]
    public async Task UnknownAssetType_Returns422_NotException()
    {
        var options = Substitute.For<IOptionsMonitor<HistoryLoaderOptions>>();
        options.CurrentValue.Returns(new HistoryLoaderOptions());
        var schemaManager = Substitute.For<ISchemaManager>();
        var feedStatusStore = Substitute.For<IFeedStatusStore>();
        var coverageCalculator = Substitute.For<IMonthCoverageCalculator>();

        var result = await CoverageEndpoints.GetCoverage(
            "binance", "BTCUSDT", "alien",
            options, schemaManager, feedStatusStore, coverageCalculator,
            TestContext.Current.CancellationToken);

        var statusResult = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, statusResult.StatusCode);
    }
}
