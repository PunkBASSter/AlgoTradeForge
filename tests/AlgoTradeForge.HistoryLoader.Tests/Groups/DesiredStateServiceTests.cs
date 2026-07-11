using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Application.Collection;
using AlgoTradeForge.HistoryLoader.Application.Groups;
using AlgoTradeForge.HistoryLoader.Application.Index;
using AlgoTradeForge.HistoryLoader.Domain.Symbology;
using AlgoTradeForge.HistoryLoader.WebApi.Groups;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Groups;

public sealed class DesiredStateServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Exchange = "binance";
    private const string Dir = "BTCUSDT_perp";

    // ---- fixture ----

    private sealed class Harness : IAsyncDisposable
    {
        public required FakeGroupStore Store { get; init; }
        public required IHistoryIndex Index { get; init; }
        public required IEagerBackfillRunner Runner { get; init; }
        public required CollectionPlanHolder Holder { get; init; }
        public required CollectionChangeNotifier Notifier { get; init; }
        public required DesiredStateService Service { get; init; }

        private int _planChanged;
        public int PlanChanged => Volatile.Read(ref _planChanged);

        public void CountPlanChanges() => Holder.PlanChanged += () => Interlocked.Increment(ref _planChanged);

        public async ValueTask DisposeAsync()
        {
            try { await Service.StopAsync(CancellationToken.None); }
            catch { /* shutdown best-effort */ }
            Service.Dispose();
        }
    }

    private static CollectionGroup PerpGroup(string collect = "eager", string historyStart = "2024-01") =>
        new(
            Name: "g1",
            Enabled: true,
            Exchanges: [Exchange],
            Assets: new GroupAssets(["BTC/USDT-PERP"], historyStart),
            Feeds: new Dictionary<string, GroupFeed>
            {
                ["candles"] = new GroupFeed(collect, ["1h"], "csv"),
            },
            Derived: null,
            SymbolOverrides: null);

    private static Harness Build(
        CollectionGroup? group = null,
        IReadOnlyList<MonthPartitionRow>? candleMonths = null,
        IEagerBackfillRunner? runner = null)
    {
        var registry = new SymbologyRegistry([new BinanceSymbology()]);
        var store = new FakeGroupStore([new GroupDocument(group ?? PerpGroup(), "etag")]);

        var index = Substitute.For<IHistoryIndex>();
        index.ListDiscoveredFirstMonths(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<DiscoveredFirstMonthRow>>([]));
        index.ListInstrumentMeta(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<InstrumentMetaRow>>(
                [new InstrumentMetaRow(Exchange, Dir, 2, 3, "0.01", "2026-01-01T00:00:00Z")]));
        index.ListAssets(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AssetIndexRow>>([]));
        index.GetMonths(Exchange, Dir, "candles", "1h", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(candleMonths ?? (IReadOnlyList<MonthPartitionRow>)[]));

        var metaProvider = Substitute.For<IInstrumentMetaProvider>();
        metaProvider.EnsureFresh(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var effectiveRunner = runner ?? Substitute.For<IEagerBackfillRunner>();
        if (runner is null)
            effectiveRunner.Run(Arg.Any<CollectionAsset>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
                .Returns(Task.CompletedTask);

        var holder = new CollectionPlanHolder();
        var notifier = new CollectionChangeNotifier();
        var evaluator = new ConvergenceEvaluator(index, registry);

        var service = new DesiredStateService(
            store, evaluator, registry, index, metaProvider, holder, effectiveRunner, notifier,
            NullLogger<DesiredStateService>.Instance);

        return new Harness
        {
            Store = store,
            Index = index,
            Runner = effectiveRunner,
            Holder = holder,
            Notifier = notifier,
            Service = service,
        };
    }

    private static async Task WaitUntil(Func<bool> predicate, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!predicate())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("condition not met within timeout");
            await Task.Delay(20, Ct);
        }
    }

    // ---- tests ----

    [Fact]
    public async Task Pipeline_PublishesPlan_AfterKick()
    {
        await using var h = Build();
        var sawFreshPlanOnPublish = false;
        h.Holder.PlanChanged += () =>
        {
            // Publish sets _current BEFORE raising PlanChanged — consumers must observe the fresh plan.
            if (h.Holder.Current.Assets.Any(a => a.Venue.Dir == Dir))
                sawFreshPlanOnPublish = true;
        };

        await h.Service.StartAsync(Ct);

        await WaitUntil(() => sawFreshPlanOnPublish);
        await WaitUntil(() => h.Runner.ReceivedCalls().Any());

        Assert.True(sawFreshPlanOnPublish);
        await h.Runner.Received().Run(
            Arg.Is<CollectionAsset>(a => a.Venue.Dir == Dir),
            Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Kick_SkipsAsset_WhenFingerprintUnchanged()
    {
        await using var h = Build();
        await h.Service.StartAsync(Ct);

        await WaitUntil(() => h.Runner.ReceivedCalls().Any());
        h.CountPlanChanges();
        var before = h.PlanChanged;

        // Same missing tuple, recompute triggered — fingerprint unchanged ⇒ no second kick.
        h.Notifier.NotifyDiscoveryRecorded();
        await WaitUntil(() => h.PlanChanged > before);

        Assert.Single(h.Runner.ReceivedCalls());
    }

    [Fact]
    public async Task Kick_Rekicks_AfterGroupsChanged()
    {
        await using var h = Build();
        await h.Service.StartAsync(Ct);

        await WaitUntil(() => h.Runner.ReceivedCalls().Count() == 1);

        // A group edit is a legitimate retry — OnGroupsChanged clears fingerprints.
        h.Store.RaiseGroupsChanged();

        await WaitUntil(() => h.Runner.ReceivedCalls().Count() == 2);
        Assert.Equal(2, h.Runner.ReceivedCalls().Count());
    }

    [Fact]
    public async Task Kick_Rekicks_WhenCoverageMoves()
    {
        await using var h = Build();
        await h.Service.StartAsync(Ct);

        await WaitUntil(() => h.Runner.ReceivedCalls().Count() == 1);

        // Coverage advanced (0 → 1 month) — fingerprint changes ⇒ re-kick even without a group edit.
        h.Index.GetMonths(Exchange, Dir, "candles", "1h", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<MonthPartitionRow>>(
                [new MonthPartitionRow("2024-01", 100, 1, "mt")]));
        h.Notifier.NotifyDiscoveryRecorded();

        await WaitUntil(() => h.Runner.ReceivedCalls().Count() == 2);
        Assert.Equal(2, h.Runner.ReceivedCalls().Count());
    }

    [Fact]
    public async Task DiscoveryRecorded_TriggersRecompute_WithoutClearingFingerprints()
    {
        await using var h = Build();
        await h.Service.StartAsync(Ct);

        await WaitUntil(() => h.Runner.ReceivedCalls().Any());
        h.CountPlanChanges();
        var before = h.PlanChanged;

        h.Notifier.NotifyDiscoveryRecorded();

        // recompute happened (fingerprints NOT cleared) ⇒ evaluator re-ran, runner did NOT.
        await WaitUntil(() => h.PlanChanged > before);
        Assert.Single(h.Runner.ReceivedCalls());
    }

    [Fact]
    public async Task KickCompletion_TriggersRecompute()
    {
        var gate = new TaskCompletionSource();
        var runner = Substitute.For<IEagerBackfillRunner>();
        runner.Run(Arg.Any<CollectionAsset>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(_ => gate.Task);

        await using var h = Build(runner: runner);
        h.CountPlanChanges();

        await h.Service.StartAsync(Ct);

        // Kick fired but the runner is blocked; the pipeline quiesces (self-trigger recompute publishes).
        await WaitUntil(() => runner.ReceivedCalls().Any());
        await WaitUntil(() => h.PlanChanged >= 2);
        var quiescent = h.PlanChanged;

        // Release the kick — its completion must Retrigger a fresh recompute (spec §3.4).
        gate.SetResult();

        await WaitUntil(() => h.PlanChanged > quiescent);
        Assert.True(h.PlanChanged > quiescent);
    }

    // ---- fakes ----

    private sealed class FakeGroupStore(IReadOnlyList<GroupDocument> docs) : IGroupStore
    {
        public event Action? GroupsChanged;

        public void RaiseGroupsChanged() => GroupsChanged?.Invoke();

        public Task<IReadOnlyList<GroupDocument>> List(CancellationToken ct = default) =>
            Task.FromResult(docs);

        public Task<GroupDocument?> Get(string name, CancellationToken ct = default) =>
            Task.FromResult(docs.FirstOrDefault(d => d.Group.Name == name));

        public Task<string> Put(string name, CollectionGroup group, string? expectedETag, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<bool> Delete(string name, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
