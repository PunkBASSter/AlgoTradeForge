using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Live.Relay;
using AlgoTradeForge.LiveHost.WebApi;
using AlgoTradeForge.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AlgoTradeForge.LiveHost.WebApi.Tests;

public class RelayPumpHostedServiceTests
{
    private sealed class FakeVenueConnector(IReadOnlyList<IMarketEvent> events) : IVenueConnector
    {
        public string Venue => "FAKE";
        public MarketDataSessionPolicy SessionPolicy => MarketDataSessionPolicy.Concurrent;
        public (sbyte PriceScaleExp, sbyte QtyScaleExp) InstrumentScale(string instrument) => (4, 3);

        public async IAsyncEnumerable<IMarketEvent> Stream(
            IReadOnlyList<string> instruments,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var ev in events)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return ev;
            }
        }
    }

    [Fact]
    public async Task Pump_ArchivesTradesAndSession()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"relay_pump_{Guid.NewGuid():N}");
        try
        {
            var trade = new TradeTick(1_700_000_000_001L, 5_000_000L, 1L, 1L, AggressorSide.Buy);
            var events = new IMarketEvent[] { new TradeEvent("BTCUSDT", trade) };

            var connector = new FakeVenueConnector(events);
            var timeProvider = new FakeTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000L));
            var storage = new LocalFileStorage(new LocalStorageOptions { DataRoot = Path.Combine(dir, "upload") });

            var opts = Options.Create(new RelayPumpOptions
            {
                LocalRoot = dir,
                KeyPrefix = "live-md",
                Instruments = ["BTCUSDT"],
                HeartbeatInterval = TimeSpan.FromMinutes(60),
                UploadInterval = TimeSpan.FromMinutes(60),
            });

            var svc = new RelayPumpHostedService(
                connector, opts, storage, timeProvider,
                NullLogger<RelayPumpHostedService>.Instance);

            await svc.RunPumpOnce(["BTCUSDT"], TestContext.Current.CancellationToken);

            // trades file exists under {root}/BTCUSDT/trades/
            var tradesDir = Path.Combine(dir, "BTCUSDT", TradeTick.StreamName);
            Assert.True(Directory.Exists(tradesDir), $"trades dir missing: {tradesDir}");
            Assert.NotEmpty(Directory.GetFiles(tradesDir, "*.atft"));

            // session stream exists under {root}/{venue}/_session/
            var sessionDir = Path.Combine(dir, connector.Venue, SessionEvent.StreamName);
            Assert.True(Directory.Exists(sessionDir), $"session dir missing: {sessionDir}");
            Assert.NotEmpty(Directory.GetFiles(sessionDir, "*.atft"));
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }
}
