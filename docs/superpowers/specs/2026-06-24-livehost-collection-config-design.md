# LiveHost — `collection.json` (reusing `DataFeedSubscription`) + execution-⊆-collected validation (Plan 6a) — Design

**Date:** 2026-06-24
**Status:** Approved (brainstorming) — ready for implementation plan
**Parent design:** `docs/superpowers/specs/2026-06-20-livehost-collection-execution-design.md` (§A cardinality, §F config model)
**Scope:** Give LiveHost a CAS-protected `collection.json` describing **what feeds the host captures**, expressed as a list of `DataFeedSubscription` (the same feed vocabulary the strategy/session config and backtest already use). Replace the static `RelayPumpOptions.Instruments` list as the source of the relay capture set, and gate live execution to what is collected — a strategy may only run on feeds the host already collects.

## Context

The parent design (§F) calls for a CAS-protected `collection.json` with per-instrument roles. Exploration (2026-06-24) reshaped this:

1. **`collection.json` does not exist yet.** Today the relay capture set is `RelayPumpOptions.Instruments`, a static `string[]` from appsettings (`src/AlgoTradeForge.LiveHost.WebApi/RelayPumpOptions.cs:7`), consumed by `RelayPumpHostedService` → `RelayIngest.Pump`. 6a creates the config.
2. **Collection is feed-granular** (a symbol implies several feeds: ticks, candles, side feeds), so a `{ symbol, role }` shape is too thin.
3. **The system already has one feed vocabulary:** `DataFeedSubscription` (`src/AlgoTradeForge.Domain/Strategy/Subscriptions/DataFeedSubscription.cs`) — a JSON-polymorphic record (`"kind"` discriminator: `TimeBar`/`AltBar`/`Tick`/`Side`) carrying `(AssetName, Exchange, Role)` + kind-specific feed identity (`TimeFrame`/`FeedId`), resolvable to a typed `Asset` via `IAssetRepository`. The strategy/session config and backtest already speak it.

So `collection.json` **reuses `DataFeedSubscription`** rather than a parallel string config. This makes the central use case — *is the strategy's subscription collected?* — a **same-type set-membership check**, not a translation between two vocabularies. It also makes the `Asset` hierarchy (not string `Symbol`/`Type` fields) the home for instrument identity, including future IB contract qualification.

The `collect | collect+execute` **role is dropped.** Its only job was the §A cardinality guardrail (archive many, execute few); with a real collected-feed set to validate against, "executable" is a *derived* property — *is this feed collected?* — not a stored flag.

### Decisions locked (brainstorming, 2026-06-24)

- **Reuse `DataFeedSubscription` + `Asset`** for the collection set. **No** parallel collection-config DTO; **no** extraction of HistoryLoader's `AssetCollectionConfig`. HistoryLoader keeps its own backfill config (it legitimately needs `HistoryStart`/`GapThresholdMultiplier`/`Enabled`, which do not belong on a subscription). The two hosts are intentionally **not** unified at the schema level — they have different metadata needs (backfill windows vs strategy roles); they share the *feed identity* vocabulary where it matters (the subscription kinds + asset model).
- **Drop `InstrumentRole`.** Execution eligibility = pure "the feeds the strategy needs are collected" validation.
- **No Domain change.** `Role` is included explicitly in `collection.json` (`Primary` for root feeds, `Side` for side feeds) and ignored by collection logic — `SideFeedSubscription` requires `Side` in its ctor, so a uniform omit-role default is not viable. The role-free-base refactor (`DataFeedRef`) is a noted future cleanup, **out of scope** for 6a (it would reach into the execution/backtest path).
- **Relay depth:** 6a derives the relay's *instrument list* from the collected `Tick` subscriptions (streamable today); capture stays **trades-only**. Per-feed relay routing (book-ticker/quotes) is incremental/future.

**Owner directive (throughout):** not in production — break freely for the cleanest end-state. No shims/aliases. `RelayPumpOptions.Instruments` is deleted, not deprecated.

## Out of scope (explicitly)

- **M6 live alt-bar partial-bar seeding (Plan 6b):** separate spec; no shared code.
- **Multi-account / per-target order routing (Plan 5):** the execution validation here is single-target; Plan 5 generalizes it.
- **HistoryLoader config changes:** untouched. No extraction, no shared lib, no migration of HistoryLoader to `collection.json`. (Converging the two config *sources* is future work, not 6a.)
- **Role-free `DataFeedRef` base refactor:** noted future cleanup; 6a only adds a default to `Role`.
- **Per-feed relay routing:** relay keeps trades-only capture; it just sources its instrument list from `collection.json`.
- **Hot-reload of the capture set:** the API writes `collection.json` live, but the relay re-reads it only on host restart (live venue subscribe/unsubscribe is complex). Stated, deferred.
- **IB contract-qualification fields:** additive on the `Asset` hierarchy when `LiveHost@ib` lands (see IB forward-compat); not built here.

## Architecture

### Collection config = list of `DataFeedSubscription`

`collection.json` (one per LiveHost; venue implicit = host's `connector.Venue`) is the serialized form of:

```csharp
public sealed record CollectionConfig(IReadOnlyList<DataFeedSubscription> Feeds);   // LiveHost.Application
```

```jsonc
// collection.json — root feeds the host captures (DataFeedSubscription polymorphic shape)
{
  "feeds": [
    { "kind": "Tick",    "assetName": "BTCUSDT", "exchange": "binance", "role": "Primary" },
    { "kind": "TimeBar", "assetName": "BTCUSDT", "exchange": "binance", "role": "Primary", "timeFrame": "1m" },
    { "kind": "Side",    "assetName": "BTCUSDT", "exchange": "binance", "role": "Side", "feedId": "funding-rate" }
  ]
}
```

- Entries are **root** feeds (`Tick`/`TimeBar`/`Side`). Derived `AltBar` feeds are *computed live* from collected roots, so they are not listed (a strategy's `AltBar` subscription validates against its root — see Validation).
- `Role` is **included explicitly** and ignored by all collection logic. It is kept rather than defaulted away: `SideFeedSubscription` *requires* `DataFeedRole.Side` in its constructor (`ValidateRole` throws otherwise), so a uniform "omit role" is impossible, and relying on `System.Text.Json` optional-ctor-param defaults is fragile. Root feeds carry `"role":"Primary"`, side feeds `"role":"Side"`. **No Domain change.** `[JsonIgnore] Asset?` / `IsExportable` are irrelevant here.

### Storage + CAS

`ICollectionConfigStore` (LiveHost.Application) + `CollectionConfigStore` (LiveHost.Infrastructure):

```csharp
public interface ICollectionConfigStore
{
    Task<StoredCollectionConfig> Load(CancellationToken ct = default);                 // empty + null ETag when absent
    Task<string> Save(CollectionConfig config, string? expectedETag, CancellationToken ct = default); // CAS; new ETag
}

public sealed record StoredCollectionConfig(CollectionConfig Config, string? ETag);
```

- Backed by `IFileStorage.ReadWithEtag(key)` / `WriteIfMatch(key, content, expectedETag)` (`src/AlgoTradeForge.Storage.Abstractions/IO/IFileStorage.cs:33-42`) at key `collection.json` — the same CAS primitive `FeedSchemaManager` uses (`FeedSchemaManager.cs:281-318`).
- Serialization uses the `DataFeedSubscription` polymorphic converter (already declared on the type) — no new converter; camelCase to match `feeds.json`.
- `Save` surfaces `ConcurrencyConflictException` on ETag mismatch (caller → 409). The store does **not** retry — a user-driven config edit; a stale write must bubble, not silently merge.
- `Load` of an absent file → empty `CollectionConfig([])` + `ETag = null`, so a first `Save` is create-only (`WriteIfMatch(..., expectedETag: null)`).

### API surface — `/api/v1/config`

Minimal-API endpoints in `AlgoTradeForge.LiveHost.WebApi`, alongside the existing `/api/live/*` group:

- `GET /api/v1/config` → 200 + `CollectionConfig` JSON; storage ETag in the HTTP `ETag` response header. Absent file → 200 with `{"feeds":[]}` and **no** `ETag` header (signals the client's `PUT` must be create-only).
- `PUT /api/v1/config` → requires `If-Match: <etag>` (omit/`*` = create-only) → `Save(config, expectedETag)`. 200 + new ETag on success; **409 Conflict** on `ConcurrencyConflictException`; 400 on a malformed body / unknown `kind`.

Standard HTTP-ETag optimistic concurrency mapped 1:1 onto the storage CAS.

### Consumers

1. **Relay pump** — `RelayPumpHostedService.ExecuteAsync` loads `collection.json` via `ICollectionConfigStore.Load()`, filters to streamable feeds (today `TickSubscription`), and projects `.AssetName` to the symbol list passed to `RelayIngest.Pump` (trades), exactly as today. `RelayPumpOptions.Instruments` is **deleted**; `RelayPumpOptions` keeps `LocalRoot`, `KeyPrefix`, `HeartbeatInterval`, `UploadInterval`. Empty/absent config → log + skip (today's guard at `RelayPumpHostedService.cs:20-24`).
2. **Session start** — `StartLiveSessionCommandHandler` validates that every strategy `DataFeedSubscription` is satisfied by the collected set, before registering the session. The collected set is matched on the **feed identity tuple** `(Exchange, AssetName, kind, feed-id)` — `Role`/`Asset`/`IsExportable` ignored:
   - `TimeBarSubscription` → a collected `TimeBar` at an interval equal to, or an even divisor of, the requested `TimeFrame` (finer collected candle derives a coarser bar; exact-match is the minimal acceptable impl if divisor logic is deferred).
   - `TickSubscription` → a collected `Tick` for the same asset.
   - `SideFeedSubscription` → a collected `Side` with the same `FeedId` (matched via `FeedKey()`).
   - `AltBarSubscription` → its **source root feed** collected, not the derived alt-bar. The source is read from the parsed `AltBarFeedId` (`AltBarFeedId.Parse(sub.FeedId)` — the same parse the bar-source resolver uses), which names `Tick` or a candle interval. Derived bars compute live from the collected root.
   - On any unmet subscription, reject the command with a clear error naming the asset + missing feed. No partial session is registered.

## Data flow

```
PUT /api/v1/config ──► CollectionConfigStore.Save ──► IFileStorage.WriteIfMatch (CAS) ──► collection.json
                                                                                              │
host start ──► RelayPumpHostedService ──► Load ──► [Tick feeds].AssetName ──► RelayIngest.Pump (trades)
                                                                                              │
StartLiveSessionCommand ──► handler ──► Load ──► for each strategy subscription: satisfied by collected set?
                                              │                                          │
                                              │                                     no ──► reject (names asset+feed)
                                              │ all yes
                                          start session
```

## Error handling

- **Stale write:** `PUT` with a stale `If-Match` → `ConcurrencyConflictException` → 409; client re-`GET`s and retries.
- **Malformed config:** unknown `kind` / unparseable body → 400 before any storage write.
- **Absent file:** `GET` → empty config (200, no `ETag`); first `PUT` is create-only.
- **Execution on uncollected feed:** session start throws a validation error naming the asset + missing feed; no partial session registered.
- **Empty capture set at startup:** relay pump logs + no-ops (unchanged).

## Testing

- **Infrastructure:** `CollectionConfigStore` — load-absent → empty+null ETag; create-only save; matching-ETag save → new ETag; stale-ETag save → `ConcurrencyConflictException`.
- **WebApi:** `GET` returns body + `ETag` header; `PUT` matching `If-Match` → 200; stale → 409; bad body → 400.
- **Application (validation):** session start rejects each kind of unsatisfied subscription (time-bar interval, tick, side, alt-bar-source); accepts when all are collected. Covers the alt-bar → root-feed derivation explicitly.
- **Relay:** `RelayPumpHostedService` sources its instrument list from the collected `Tick` feeds; empty/absent → skip. The `RelayPumpOptions.Instruments` deletion leaves the relay tests green (rewritten to seed via the store).

## Verification

Done when: `collection.json` is readable/writable through `/api/v1/config` under CAS (409 on stale write); the relay pump captures exactly the instruments whose `Tick` feed is collected, with `RelayPumpOptions.Instruments` deleted; a live session is rejected unless every subscription is satisfied by the collected set; and all tests above pass. No engine hot path is touched, so no benchmark run is required for 6a.

## IB forward-compatibility (designed-for, not built here)

The IB-connector POC (`poc/ib-connector/src/Contracts.cs:7-22`) confirms an IB instrument is a `Contract` of `Symbol + SecType + Exchange + PrimaryExch + Currency` → `ConId`; `"AAPL"` alone is ambiguous. Reusing `DataFeedSubscription` + `Asset` makes this **cleaner**, not harder: IB contract identity belongs on the `Asset` hierarchy (a new asset subtype resolved by `IAssetRepository`), and a subscription references its asset by `(AssetName, Exchange)`. When `LiveHost@ib` lands (Plan-5-adjacent), the asset resolution gains the IB qualification; `collection.json` and the validation seam are unchanged in shape. No 6a work needed — the reuse decision *is* the forward-compat story.

## Conventions / gotchas

- One type per file (`CollectionConfig`, `ICollectionConfigStore`, `CollectionConfigStore`, `StoredCollectionConfig`); `CollectionConfig`/store types live in LiveHost.Application/Infrastructure (LiveHost-only config).
- No `Async` suffix on the new async store methods (`Load`/`Save`); `CancellationToken ct = default` on every async I/O method; `using`-over-try/finally.
- LiveHost must not depend on HistoryLoader; the reused `DataFeedSubscription`/`Asset` live in `AlgoTradeForge.Domain`, which LiveHost already references.
- No Domain change; `collection.json` reuses the existing `DataFeedSubscription` polymorphic JSON contract unchanged.
- JSON: camelCase, reusing the `DataFeedSubscription` polymorphic converter so `collection.json` reads consistently with session configs.
