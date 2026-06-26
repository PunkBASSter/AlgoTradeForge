# LiveHost — Plan 1 — Venue-neutral Contract Identity — Design

**Date:** 2026-06-26
**Status:** Approved (brainstorming) — ready for writing-plans
**Spine:** Root of the IB re-plan spine `1 → {2,3} → 4 → 5` (`2026-06-25-livehost-ib-replan-phase-design.md`). Plan 1 unblocks Plan 2 (`IOrderRouter`/multi-account) and Plan 3 (`IbVenueConnector`).
**Review tier:** sonnet (per the phase spec).

## Scope

Establish **Interactive Brokers contract identity** as the first concern of the IB venue slice, reusing the Domain `Asset` hierarchy for strategy/economics polymorphism and letting the venue slice own the IB-specific contract model. Concretely, Plan 1 delivers — all in `src/AlgoTradeForge.LiveHost.Infrastructure/Live/InteractiveBrokers/` unless noted:

1. **Vendored IBApi** integrated into the production solution (TWS API 10.45.01).
2. A **long-term IB transport primitive** (`IbConnection`) — connect + `EReader` pump + `reqId → TaskCompletionSource` correlator — wired to the POC-verified paper credentials/port/clientId. This is the seed Plan 3 grows `IbSession` around; **not a throwaway**.
3. The **two-tier `IbContract` model** (configured tier + resolved tier).
4. A **polymorphic `Asset` ↔ `IbContract` mapper** that mirrors the existing `GetSettlementCalculator(this Asset)` extension-dispatch idiom.
5. A **real `IbContractResolver`** (`reqContractDetails` + cache) validated against live paper IB.

**Out of scope** (later plans): data plane / `IVenueConnector` bridge (Plan 3), order plane / `IbOrderSession` (Plan 4), `IOrderRouter`/multi-account (Plan 2), deployment (Plan 5), multi-currency PnL (deferred open point).

## Non-negotiable constraints (carried from the phase spec)

- **Domain stays venue-neutral and zero-ProjectReferences.** No IB vocabulary (`ConId`, `SecType`, `SMART`, `Currency`, `LocalSymbol`) enters Domain. All IB types live in the Infrastructure slice.
- `LiveHost.Infrastructure` may reference the vendored IBApi project; **Domain/Application may not.**
- One type per file; no `Async` suffix on new async methods; `using`-over-`try`/`finally`; Int64 money via `MoneyConvert.ToLong` in Domain and `ScaleContext` at boundaries (mostly N/A in Plan 1 — no tick scaling here).
- ONE `dotnet` process at a time (build/test strictly sequential); `powershell.exe`, never `pwsh`.

## Design

### 1. Reuse the Domain `Asset` hierarchy (the OOP core)

The Domain `Asset` hierarchy (`src/AlgoTradeForge.Domain/Assets/`) is already polymorphic: `Multiplier` and `Settlement` are `abstract`/`override`, `ComputeAutoApplyDelta` is `virtual`/`override`, and the `ICashSettledAsset`/`IMarginAsset` marker split distinguishes settlement economics. An IB AAPL position **is** an `EquityAsset` → `CashAndCarrySettlement` with zero new domain logic.

The IB contract shape is **derived polymorphically from this hierarchy**, not from a parallel config dictionary. The canonical house idiom is `SettlementCalculatorExtensions.GetSettlementCalculator(this Asset)` — an extension method that switches on a polymorphic Asset property and returns a domain abstraction. Plan 1's mapper is the same idiom, located in the venue slice (so IB vocabulary stays out of Domain), keyed on the concrete asset **type** because `Settlement` alone cannot discriminate `SecType` (`EquityAsset` and `CryptoAsset` are both `CashAndCarry`; `FutureAsset` and `CryptoPerpetualAsset` are both `Margin`).

Field derivation:

| IbContract field | Source |
|---|---|
| `Symbol` | `asset.Name` |
| `SecType` | the concrete asset **type** (polymorphic discriminator) |
| `PrimaryExch` | `asset.Exchange` (the listing exchange, e.g. `"NASDAQ"`) |
| `Exchange` (routing) | constant `"SMART"` (IB routing default) |
| `Currency` | constant `"USD"` (default; carried on `IbContract` so the deferred multi-currency case is expressible without building it) |
| `Multiplier` | `asset.Multiplier` (the overridden value — variable for `FutureAsset`, `1` otherwise) |
| `minTick` | `asset.TickSize` |

**Decision (locked):** `Asset.Exchange` denotes the **listing/primary exchange** (`"NASDAQ"`), mapped to IB `PrimaryExch`. The orthogonal *which venue connector serves this asset* axis (IB vs Binance) is a Plan 3 concern and is not resolved here.

**Decision (locked):** No `Currency` (or any IB field) is added to Domain. Multi-currency PnL stays deferred (open point #1).

### 2. The `IbContract` model (two-tier)

One type per file, in the slice, **no IBApi reference** on these records:

- **Configured tier** — `IbContract` record: `Symbol`, `SecType` (`IbSecType` enum), `Exchange` (routing — `"SMART"` for stocks, the direct futures exchange e.g. `"COMEX"` for futures), `PrimaryExch` (listing exchange for stocks, empty for futures), `Currency`. Value equality makes it the natural resolver cache key and gives a clean config round-trip.
- **Resolved tier** — `ResolvedIbContract(IbContract Spec, int ConId, string LocalSymbol, string LastTradeDate)` (`LastTradeDate` carries the chosen futures front-month expiry; empty for equities).
- **`IbSecType` enum** — `Stk`, `Fut`. House-consistent with the `SettlementMode` enum. Mapped to IB's wire string (`"STK"`/`"FUT"`) only at the IBApi boundary.

**Decision (locked):** `SecType` is a typed enum (not raw strings), and the resolved tier is a **separate record** (not a half-populated `IbContract` with a nullable `ConId`).

### 3. Mapper — `Asset` ↔ `IbContract`

Extension class in the slice (mirrors `SettlementCalculatorExtensions`). The Exchange/PrimaryExch split is polymorphic per asset kind — stocks route via `SMART` with a primary-listing exchange; futures route to their direct exchange with no primary, and are resolved to a front-month conId later by the resolver:

```csharp
public static IbContract ToIbContract(this Asset asset) => asset switch
{
    EquityAsset          => /* STK,  Exchange=SMART,         PrimaryExch←Exchange, USD */,
    FutureAsset          => /* FUT,  Exchange←Asset.Exchange, PrimaryExch="",       USD (front-month resolved later) */,
    CryptoAsset          => throw new NotSupportedException("IB crypto is PAXOS-routed, not a Binance-spot asset"),
    CryptoPerpetualAsset => throw new NotSupportedException("IB has no crypto perpetuals"),
    _ => throw new ArgumentOutOfRangeException(nameof(asset)),
};
```

Reverse direction `ToAsset(this ResolvedIbContract)` dispatches on `SecType` and constructs the matching Domain `Asset` record. **Plan 1 implements `Stk` → `EquityAsset` only;** `Fut` → `FutureAsset` reverse-reconstruction needs `contractDetails` multiplier/minTick enrichment and is **deferred to Plan 2/4** reconciliation (open point #3), where IB pushes positions/open orders back as contracts and the portfolio/settlement math needs a Domain `Asset`.

**Decision (locked):** Forward mapping is **equities + commodity/metal/index futures**. `FutureAsset → FUT` needs no Domain expiry field — the resolver requests the expiry-less family, enumerates all listed months via `contractDetails`/`contractDetailsEnd`, and picks the nearest non-expired (front month). `CryptoAsset`/`CryptoPerpetualAsset → ToIbContract` **throw** `NotSupportedException` (IB crypto is PAXOS-routed and distinct from a Binance-spot asset; IB offers no crypto perpetuals). Options and single-stock futures are deferred (no Domain `OptionAsset` yet); the `_` arm guards future additions.

### 4. Transport primitive — `IbConnection`

A focused, long-term transport primitive (not a throwaway), built from the POC's proven shape:

- Owns the single `EClientSocket`, the `EReaderSignal`, and the `EReader` pump thread.
- `Connect(host, port, clientId, …)` / `Disconnect` — paper port `:4004`, unique `clientId`, credentials/host via configuration (POC: `andrewpapertest` paper login behind the gnzsnz gateway). Connection robustness (retry/backoff, gateway cold-start tolerance, 10141 paper-disclaimer self-heal handled by IBC) carried from the POC.
- An `IbWrapper : DefaultEWrapper` routes the callbacks Plan 1 needs (`nextValidId`, `contractDetails`, `contractDetailsEnd`, `error`) and completes correlated `TaskCompletionSource`s.
- Request-reply correlator: `ConcurrentDictionary<int, …>` keyed by `reqId`. **Accumulates** each `contractDetails(reqId, …)` and completes on `contractDetailsEnd(reqId)` (a single `reqContractDetails` returns many months for a futures family — the POC's "complete on first" shortcut is corrected here); an `error(reqId, …)` with `id >= 0` faults the awaiter.

Plan 3 grows `IbSession` around this exact primitive (adds tick-by-tick + `reqRealTimeBars` streaming and shares the socket with the Plan 4 order plane). The `MarketDataSessionPolicy` capability already exists (`AlgoTradeForge.Live.Relay.MarketDataSessionPolicy { Concurrent, SingleSession }`); IB's `SingleSession` wiring is a Plan 3 concern.

### 5. Resolver — `IbContractResolver`

`IIbContractResolver` interface + `IbContractResolver` impl in the slice:

```csharp
Task<ResolvedIbContract> Resolve(IbContract spec, CancellationToken ct = default);
```

- Cache hit by `IbContract` value equality → return cached `ResolvedIbContract`.
- Miss → translate `spec` → `IBApi.Contract` (the `IbContract → IBApi.Contract` translation lives in the slice and is reused by Plans 3/4), `reqContractDetails(reqId, contract)` via `IbConnection`, await the correlated `TaskCompletionSource`, read `ConId` + `LocalSymbol` from `contractDetails`, cache, return.
- Cache: `ConcurrentDictionary<IbContract, ResolvedIbContract>` (single resolution per configured identity per process lifetime).

This is the **full real resolver** validated against live paper IB (owner decision), hardening the POC's verified `reqContractDetails` path rather than stubbing it.

### 6. Vendored IBApi integration

- The vendored IBApi project (`AlgoTradeForge.IbApi.csproj`: `net10.0`, `OutputType=Library`, `<Nullable>disable</Nullable>`, `<ImplicitUsings>disable</ImplicitUsings>`, `<TreatWarningsAsErrors>false</TreatWarningsAsErrors>` — overriding the root props — `AssemblyName/RootNamespace = IBApi`, `PackageReference Google.Protobuf 3.29.5`) plus the TWS API 10.45.01 C# source (271 `.cs`) + `protobuf/` folder are vendored **in-tree** at `src/AlgoTradeForge.IbApi`. Referenced in `AlgoTradeForge.slnx`.
- `LiveHost.Infrastructure` adds a `ProjectReference` to it. No other project references it.

**Decision (locked — owner-directed, settled after a brief submodule detour):** the vendored IBApi is **committed in-tree** at `src/AlgoTradeForge.IbApi`, **marked as external code** via the repo-root `.gitattributes` (`src/AlgoTradeForge.IbApi/** linguist-vendored=true` — GitHub collapses it in diffs + excludes it from language stats) plus a README banner and the csproj "not ours to maintain" comment; a nested `.gitignore` keeps `bin/obj` untracked. Rationale: the exact stable 10.45.01 the money-host builds against is backed up here with **no submodule-init / external-fetch step** on CI or clean checkouts. (A private git submodule `PunkBASSter/twsapi-vendor` was implemented first to keep 271 files out of the tree, then reversed in favor of an in-tree backup — the orphan vendor repo can be deleted. No public IBApi package matches 10.45.01 — only an outdated 9.76.1 — so vendoring is required either way.)

## Verification

- **Unit (no socket, fast, deterministic):**
  - `ToIbContract` for equity (STK/SMART/PrimaryExch) and futures (FUT/direct-exchange/no-primary), plus the `CryptoAsset`/`CryptoPerpetualAsset` → `NotSupportedException` arms.
  - `ToAsset` for `Stk` → `EquityAsset` (the `Fut` reverse arm throws — deferred to Plan 2/4).
  - `IbWrapper` correlator: accumulate `contractDetails` → complete on `contractDetailsEnd`; `error(reqId)` faults; `error(-1)` ignored.
  - `FuturesFrontMonthSelector`: nearest non-expired pick, `yyyymmdd`/`yyyymm` parsing, all-expired / empty throw.
  - `IbContractResolver` cache behavior (miss resolves once, hit returns cached, distinct specs resolve independently) against a **fake** `IIbContractDetailsClient` — no real socket.
- **Integration (gated, POC-style):** with the gnzsnz paper gateway running (`compose up`, `:4004`, `IB_PAPER_HOST` set), the resolver returns a non-zero `ConId` for `{AAPL,STK,SMART,USD}` (expected `LocalSymbol`) **and** for the `{GC,FUT,COMEX,USD}` gold future (a concrete front-month expiry). Contract resolution needs no market-data entitlement (works off-hours). Trait-gated (`Category=IbPaper`) so CI (no gateway) skips it; runnable locally.
- **Build:** full solution `dotnet build AlgoTradeForge.slnx` clean (vendored IBApi compiles nullable-off without leaking warnings into the rest of the solution).

## Open points (deferred, flagged — not built in Plan 1)

1. **Multi-currency PnL/portfolio.** `IbContract.Currency` exists and is carried, but multi-currency account math (a Domain-adjacent concern) is out of scope for the USD paper endpoint; revisit when a non-USD instrument goes live.
2. **Venue-connector selection axis** (which connector serves a given asset: IB vs Binance). `Asset.Exchange` holds the listing exchange; the connector-selection signal is a Plan 3 wiring concern.
3. **`TickSize`/`Multiplier` enrichment from `contractDetails`** (IB returns `minTick`, contract multiplier) on the reverse `ToAsset` path — wired where reconciliation actually consumes it (Plan 2/4); Plan 1's reverse mapper builds a correct Domain record with the information available.
4. **Live-login 2FA** — LIVE-ONLY (Spike S); paper needs none, so it never touches Plan 1.

## Conventions / process

- Fresh branch `feat/livehost-plan1-contract-identity` off `main` (`main` is current — catch-up replay merged).
- SDD: the implementer subagent's `git add` is hook-denied; the **controller stages + commits per task after task-review passes** (per-branch owner authorization). Commit messages via bash heredoc + `git commit -F` (never PowerShell `Out-File` — UTF-8 BOM); end with the `Co-Authored-By` + `Claude-Session` trailers. Owner squashes/merges; do not open a PR without explicit say-so.
