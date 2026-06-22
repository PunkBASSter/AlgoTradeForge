# Handover — LiveHost Housekeeping Phase (subscription unification + capability split)

**Purpose:** kick off the housekeeping/refactor phase that precedes Plan 5/6. Paste the
prompt block below into a fresh session. Scope detail lives in
`docs/superpowers/specs/2026-06-23-livehost-data-plane-followups.md`.

---

Continue the AlgoTradeForge LiveHost decomposition. Plan 4 (the data plane) is complete.
Before Plan 5 (multi-account IOrderRouter) and Plan 6 (collection.json roles + M6 live
alt-bars), execute the HOUSEKEEPING phase: the subscription-model unification + strategy
capability split + carried fixes that Plan 4 surfaced. This phase reshapes the subscription
and strategy-callback contracts that Plans 5/6 build on, so it goes first.

READ FIRST (authoritative, in order):
- docs/superpowers/specs/2026-06-23-livehost-data-plane-followups.md — THE scope for this
  phase (§A subscription unification, §A′ PrimaryAsset, §B bar capability split, §C
  IQuoteTickStrategy [defer], §D OCE-filter fix, §E cosmetic cleanups) with blast radius +
  sequencing.
- Memory notes (auto-loaded via MEMORY.md): project_strategy_framework_v2 (the deferred
  items + why), project_service_decomposition (Plan 4 complete; Plans 5/6 next),
  feedback_no_auto_staging, feedback_oce_filter_pattern, feedback_single_process,
  feedback_using_over_try_finally.
- docs/superpowers/specs/2026-06-22-livehost-data-plane-design.md + its plan
  (…/plans/2026-06-22-livehost-data-plane.md, incl. the As-Built Amendments) — what the data
  plane shipped.
- docs/diagrams/livehost-data-plane/*.mmd — current data-flow (component map + per-input-type
  sequences).

CURRENT STATE (verify with git before trusting — owner restructures/squashes his own
branches; do NOT auto-restore a reset HEAD, check reflog):
- Confirm whether the Plan 4 branch (feat/livehost-data-plane) is merged to main or still
  in flight; pull main; branch fresh per item.
- As-built facts this phase depends on:
  * The alt-bar accumulator engine lives in AlgoTradeForge.Domain/Aggregation (IBarAccumulator,
    8 accumulators internal, AccumulatorEntry factory with private AssertScalesMatch,
    ThresholdResolver + ResolveParsed, ThresholdValue, AltBarFeedId, StreamingMedianEstimator,
    TickToSourceRecord). Domain keeps ZERO ProjectReferences.
  * Live routing is CAPABILITY-DRIVEN: LiveEventRouting was DELETED. Bars→IInt64BarStrategy,
    fills→every IStrategy, trade-ticks→ITradeTickStrategy, OnBarStart→venue sources that
    support it (KlineVenueBarSource only). Data plane: ITickRouter/IStrategyDispatch/
    IBarSourceResolver/IBarSource(+ITickFedBarSource) in LiveHost.Application, impls in
    LiveHost.Infrastructure; host-singleton lifetimes, connector per-account.
  * LiveSessionConfig still carries the DUAL list (Subscriptions resolved + RawSubscriptions
    typed, 1:1 positional, SessionInterest length-guard) + PrimaryAsset — these are §A/§A′
    targets.

THE WORK (each item = its own brainstorm → writing-plans → subagent-driven-development; do
NOT batch them into one mega-plan):
- §D first (independent, HIGH priority, money host): fix the pre-existing OCE-filter in
  BinanceLiveConnector's reconciliation TIMER loop (~catch when (ex is not OCE) →
  IsTrueShutdown/when ct.IsCancellationRequested), with a focused test. Small own branch.
- §A + §A′ + §B together (the bulk — Strategy Framework v2 subscription unification): retire
  flat DataSubscription(Asset, TimeFrame) for ONE resolved-typed model (DataFeedSubscription
  hierarchy carrying the resolved Asset, e.g. ResolvedSubscription{Spec, Asset}); collapse
  LiveSessionConfig's dual list; re-key MarketDataSnapshot (it uses DataSubscription as its
  DICTIONARY KEY — the hard part); model PrimaryAsset as the primary/trade-subscription;
  split IInt64BarStrategy into IBarStrategy.OnBarComplete + an OnBarStart capability. Blast
  radius ~73 src files through the CORE engine (BacktestEngine.EmitBar, IIndicatorFactory,
  every strategy/module, backtest/optimization/validation/persistence). Existing resolver
  seam to build on: StrategySubscriptionFactory.FromPrimary(DataFeedSubscription, Asset).
  Backtest must stay behavior-identical (golden/engine tests are the guard); backtest calls
  OnBarStart/OnBarComplete directly and must keep working.
- §E cosmetic cleanups: fold opportunistically into the above (unused _logger in TickRouter/
  StrategyDispatch; Program.cs FQ DI names; double ToList in GetSessionSnapshotAsync; UTF-8
  BOM on ~10 relocated engine test files; missing tick-sub-at-[0] flat-Bars fallback test).
- §C (IQuoteTickStrategy.OnQuoteTick + QuoteEvent dispatch tap): DEFER — YAGNI until a
  quote-driven strategy exists. QuoteTick is BBO state, does NOT aggregate to bars/SourceRecord.

OWNER DIRECTIVE: "break strategies freely — they are garbage." This lifts the strategy-BODY
rewrite cost (rewrite/delete strategies as needed), but does NOT shrink the §A core blast
radius (engine + MarketDataSnapshot key + indicators + optimization are core, not garbage).
Breaking the live API request shape is also acceptable (not in prod).

CONVENTIONS / GOTCHAS (critical):
- ONE dotnet process at a time (build/test strictly sequential). Use powershell.exe, not pwsh.
- Int64 money: MoneyConvert.ToLong in Domain, ScaleContext at boundaries; volume scales by
  quantity scale, prices by FromMarketPrice — never raw casts on money.
- Every channel bounded; order/execution path independent of market data; no
  catch when (ex is not OperationCanceledException) in long-running loops; no sync-over-async
  at production call sites; no Async suffix on new async methods; using-over-try/finally;
  one type per file.
- Domain stays ZERO ProjectReferences. LiveHost must not depend on HistoryLoader;
  Live.Relay must not depend on LiveHost.
- Perf/alloc regressions go through the BenchmarkDotNet harness (run-benchmarks), not ad-hoc
  asserts.
- COMMITS: standing rule is no-auto-staging; the SDD subagent git-add is DENIED by a hook, so
  implementers must NOT commit — the CONTROLLER stages + commits on their behalf after
  verifying the diff (get explicit per-branch commit authorization from the owner first).
  After any commit run git status --porcelain + verify git log/reflog. The owner
  resets/squashes branches himself — do NOT auto-restore reset history; if HEAD looks reset,
  inspect reflog and ask.
- Commit messages via bash heredoc + git commit -F (never PowerShell Out-File — UTF-8 BOM);
  end with the Co-Authored-By + Claude-Session trailers.
- Run the superpowers flow exactly as Plans 1–4: brainstorming → writing-plans →
  subagent-driven-development (fresh implementer per task; per-task two-verdict review;
  opus for the concurrency/engine-critical tasks like the MarketDataSnapshot re-key and the
  callback-contract change; final whole-branch opus review + a backtest≡unchanged golden
  guard). SDD ledger lives at $(git rev-parse --git-path sdd)/progress.md.

YOUR TASK:
1. Verify git state (Plan 4 merged? branch fresh off main). Confirm the as-built facts above.
2. Confirm scope/sequencing with me (especially: do §A+§A′+§B as one unification effort or
   split; where the resolved Asset lives — ResolvedSubscription wrapper vs Asset on the
   hierarchy; how MarketDataSnapshot re-keys).
3. Then run brainstorming → writing-plans → subagent-driven-development per item, §D first,
   then the §A/§A′/§B unification, before Plan 5/6.
