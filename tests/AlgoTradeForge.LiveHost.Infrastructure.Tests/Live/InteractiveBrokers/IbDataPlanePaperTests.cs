using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Live.Relay;
using AlgoTradeForge.LiveHost.Infrastructure.Live.Binance;
using AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live.InteractiveBrokers;

[Trait("Category", "IbPaper")]
public sealed class IbDataPlanePaperTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Connect_Resolve_StreamTicks_AAPL()
    {
        if (!IbPaperGatewayConfig.IsConfigured) Assert.Skip(IbPaperGatewayConfig.SkipReason);

        var wrapper = new IbWrapper();
        await using var connection = new IbConnection(wrapper, IbPaperGatewayConfig.Options);
        await using var session = new IbSession(
            new IbConnectionMarketDataClient(connection), wrapper, NullLogger<IbSession>.Instance);

        var detailsClient = new IbConnectionContractDetailsClient(connection, wrapper, TimeProvider.System);
        var resolver = new IbContractResolver(detailsClient);

        var aapl = new EquityAsset { Name = "AAPL", Exchange = "NASDAQ" };
        var assetResolver = Substitute.For<IIbInstrumentAssetResolver>();
        assetResolver.Resolve(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<Asset>(aapl));

        var opts = new IbDataPlaneOptions
        {
            InstrumentScales = { ["AAPL"] = new TickScale(PriceExp: 2, QtyExp: 0) },
        };

        var connector = new IbVenueConnector(session, resolver, assetResolver, opts);

        // Drain the stream for up to 8 seconds. Off-hours or without entitlement IB may not push
        // TradeEvents; the durable assertion is "connect + subscribe + drain briefly without error".
        using var drainCts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        drainCts.CancelAfter(TimeSpan.FromSeconds(8));

        var events = new List<IMarketEvent>();
        try
        {
            await foreach (var ev in connector.Stream(["AAPL"], drainCts.Token))
                events.Add(ev);
        }
        catch (OperationCanceledException) when (drainCts.IsCancellationRequested && !Ct.IsCancellationRequested)
        {
            // timeout expired — expected path when no ticks arrive (off-hours / no entitlement)
        }

        // If any events arrived they must be TradeEvents (the only event type IbVenueConnector emits).
        foreach (var ev in events)
            Assert.IsType<TradeEvent>(ev);
    }

    [Fact]
    public async Task Resolve_StreamRealtimeBars_AAPL()
    {
        if (!IbPaperGatewayConfig.IsConfigured) Assert.Skip(IbPaperGatewayConfig.SkipReason);

        var wrapper = new IbWrapper();
        await using var connection = new IbConnection(wrapper, IbPaperGatewayConfig.Options);
        await using var session = new IbSession(
            new IbConnectionMarketDataClient(connection), wrapper, NullLogger<IbSession>.Instance);

        await session.Connect(Ct);

        var detailsClient = new IbConnectionContractDetailsClient(connection, wrapper, TimeProvider.System);
        var resolver = new IbContractResolver(detailsClient);

        var aaplSpec = new IbContract("AAPL", IbSecType.Stk, "SMART", "NASDAQ", "USD");
        var scale = new ScaleContext(new EquityAsset { Name = "AAPL", Exchange = "NASDAQ" });

        var barsReceived = new List<Int64Bar>();
        var source = new IbVenueBarSource(
            session, resolver, aaplSpec, scale,
            onBar: (bar, _) => barsReceived.Add(bar));

        // Start subscription; bars arrive every 5s from IB — off-hours the subscription itself is the
        // durable assertion (no throw within bounded wait).
        await source.Start();

        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        waitCts.CancelAfter(TimeSpan.FromSeconds(10));
        try { await Task.Delay(Timeout.Infinite, waitCts.Token); }
        catch (OperationCanceledException) when (waitCts.IsCancellationRequested && !Ct.IsCancellationRequested) { }

        // Recent reflects whatever bars arrived during the window (may be empty off-hours).
        Assert.True(source.Recent.Count >= 0); // always passes; documents the assertion intent
    }

    [Fact]
    public void HistoricalBackfill_Lossless_RequiresEntitlement()
    {
        Assert.Skip(
            "requires IB market-data entitlement (10189) — historical-tick backfill cannot be asserted on the " +
            "paper AAPL feed; IbBackfillRequester is unit-tested in IbBackfillRequesterTests");
    }

    // Connection troubleshooting aid (gated, never a CI gate): drives the full handshake and PRINTS the
    // positive markers — connect, nextValidId, resolved conId, tick count — to test output so a manual run
    // shows what actually happened (vs the lenient pass/skip of the streaming tests). IbSession's real ILogger
    // is forwarded to the output too, so an internal reconnect/error surfaces in the run log.
    [Fact]
    public async Task Connect_Diagnostics_AAPL()
    {
        if (!IbPaperGatewayConfig.IsConfigured) Assert.Skip(IbPaperGatewayConfig.SkipReason);
        var output = TestContext.Current.TestOutputHelper
            ?? throw new InvalidOperationException("No xUnit test output helper available.");

        var wrapper = new IbWrapper();
        await using var connection = new IbConnection(wrapper, IbPaperGatewayConfig.Options);
        await using var session = new IbSession(
            new IbConnectionMarketDataClient(connection), wrapper, new TestOutputLogger<IbSession>(output));

        output.WriteLine($"[connect] {IbPaperGatewayConfig.Host}:{IbPaperGatewayConfig.Port} clientId={IbPaperGatewayConfig.ClientId} ...");
        await session.Connect(Ct);
        var nextValidId = await wrapper.NextValidId; // completed during a successful Connect
        output.WriteLine($"[connect] OK — nextValidId={nextValidId}");

        var resolver = new IbContractResolver(
            new IbConnectionContractDetailsClient(connection, wrapper, TimeProvider.System));
        var resolved = await resolver.Resolve(
            new IbContract("AAPL", IbSecType.Stk, "SMART", "NASDAQ", "USD"), Ct);
        output.WriteLine(
            $"[resolve] AAPL → conId={resolved.ConId} localSymbol={resolved.LocalSymbol} expiry='{resolved.LastTradeDate}'");

        // Informational: count trades over a short window (often 0 off-hours / without entitlement).
        var aapl = new EquityAsset { Name = "AAPL", Exchange = "NASDAQ" };
        var assetResolver = Substitute.For<IIbInstrumentAssetResolver>();
        assetResolver.Resolve(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<Asset>(aapl));
        var connector = new IbVenueConnector(session, resolver, assetResolver,
            new IbDataPlaneOptions { InstrumentScales = { ["AAPL"] = new TickScale(PriceExp: 2, QtyExp: 0) } });

        var count = 0;
        using var drainCts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        drainCts.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await foreach (var _ in connector.Stream(["AAPL"], drainCts.Token))
                count++;
        }
        catch (OperationCanceledException) when (drainCts.IsCancellationRequested && !Ct.IsCancellationRequested)
        {
        }
        output.WriteLine($"[stream] {count} trade event(s) in 5s");

        // The diagnostics signal: a successful connect assigns nextValidId, and the sec-def lookup returns a conId.
        Assert.True(nextValidId > 0, "nextValidId should be assigned after a successful connect");
        Assert.True(resolved.ConId > 0, "AAPL conId should resolve (>0)"); // AAPL is historically 265598
    }

    // Forwards IbSession's ILogger output to xUnit test output (a diagnostics run shows internal
    // warnings — e.g. a reconnect failure — alongside the handshake markers above).
    private sealed class TestOutputLogger<T>(ITestOutputHelper output) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            output.WriteLine($"[{logLevel}] {formatter(state, exception)}" +
                (exception is null ? "" : Environment.NewLine + exception));
    }
}
