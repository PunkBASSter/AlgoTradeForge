using AlgoTradeForge.Application.CandleIngestion;
using AlgoTradeForge.Live.Relay;
using AlgoTradeForge.LiveHost.Application.Collection;
using AlgoTradeForge.LiveHost.Application.Live.DataPlane;
using AlgoTradeForge.LiveHost.Application.Live.Recovery;
using AlgoTradeForge.LiveHost.Infrastructure.Live.Binance;
using AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;
using AlgoTradeForge.LiveHost.Infrastructure.Live.Recovery;
using AlgoTradeForge.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live.InteractiveBrokers;

// DI registration smoke test: verifies AddIbDataPlane populates the container with
// the correct service types. Full resolution (BuildServiceProvider) requires
// Microsoft.Extensions.DependencyInjection (non-abstractions) not yet in this test project;
// descriptor-level assertions cover the wiring without that dependency.
public class IbDataPlaneRegistrationTests
{
    [Fact]
    public void AddIbDataPlane_RegistersVenueConnector_BarSourceResolver_BackfillRequester()
    {
        var services = new ServiceCollection();

        // Pre-populate external deps the graph expects at resolution time
        services.AddSingleton(Substitute.For<ICollectionConfigStore>());
        services.AddSingleton(Substitute.For<IFileStorage>());
        services.AddSingleton(Substitute.For<IReplaySource>());
        services.AddSingleton(Substitute.For<IInt64BarLoader>());
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(new CatchupOptions
        {
            RelayKeyPrefix = "live-md",
            DataRoot = Path.GetTempPath(),
        });

        var config = new ConfigurationBuilder().Build();
        services.AddIbDataPlane(config);

        var descriptors = services.ToList();

        Assert.Contains(descriptors, d => d.ServiceType == typeof(IVenueConnector));
        Assert.Contains(descriptors, d => d.ServiceType == typeof(IBarSourceResolver));
        Assert.Contains(descriptors, d => d.ServiceType == typeof(IBackfillRequester));
        Assert.Contains(descriptors, d => d.ServiceType == typeof(IIbMarketDataSession));
        Assert.Contains(descriptors, d => d.ServiceType == typeof(IIbContractResolver));
        Assert.Contains(descriptors, d => d.ServiceType == typeof(IIbInstrumentAssetResolver));
    }

    // Proves InstrumentScales and scalar properties survive configuration binding.
    // Uses GetSection("Ib").Bind() directly (no BuildServiceProvider needed) since
    // the test project only references DI abstractions.
    [Fact]
    public void IbDataPlaneOptions_BindsInstrumentScales_AndMaxGapMs_FromConfiguration()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ib:InstrumentScales:AAPL:PriceExp"] = "2",
                ["Ib:InstrumentScales:AAPL:QtyExp"] = "0",
                ["Ib:InstrumentScales:MSFT:PriceExp"] = "3",
                ["Ib:InstrumentScales:MSFT:QtyExp"] = "1",
                ["Ib:MaxGapMs"] = "12345",
                ["Ib:Host"] = "192.168.1.1",
                ["Ib:Port"] = "7497",
            })
            .Build();

        var opts = new IbDataPlaneOptions();
        config.GetSection("Ib").Bind(opts);

        Assert.Equal(12345L, opts.MaxGapMs);
        Assert.Equal("192.168.1.1", opts.Host);
        Assert.Equal(7497, opts.Port);
        Assert.True(opts.InstrumentScales.ContainsKey("AAPL"), "AAPL scale missing");
        Assert.Equal(2, opts.InstrumentScales["AAPL"].PriceExp);
        Assert.Equal(0, opts.InstrumentScales["AAPL"].QtyExp);
        Assert.True(opts.InstrumentScales.ContainsKey("MSFT"), "MSFT scale missing");
        Assert.Equal(3, opts.InstrumentScales["MSFT"].PriceExp);
        Assert.Equal(1, opts.InstrumentScales["MSFT"].QtyExp);
    }
}
