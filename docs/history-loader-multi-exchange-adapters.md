# HistoryLoader — multi-exchange adapters (findings + direction)

**Status:** design notes, not a committed plan. Findings from a code read on 2026-07-12 (branch `feat/group-driven-collection-phase3a`). Captures where the exchange-extension seam stands and what adding a second/third CEX adapter (Gate.io, KuCoin) would actually require. No tasks, no decision yet — input for a future brainstorm.

## TL;DR

The **symbology/dispatch layer** (phases 2–3a) is already exchange-pluggable. The **collection execution layer** (collectors, stream services, REST clients, feed schemas) is a Binance monolith. The plan knows the exchange; the executor ignores it. Adding Gate/KuCoin today is **not** a parallel drop-in — it forces changes to existing services or a parallel Binance-shaped stack. The target is to extend the `(exchange, feed) → implementation` dispatch that `ArchiveMaterializerRegistry` already uses from the archive layer to the whole collection layer.

## 1. Connector overlap with LiveHost — currently none

`HistoryLoader.*` and `LiveHost.*` share **no project references** (only `Domain` + `Storage`, plus HL.Infrastructure → thin `Live.Relay`). The WebSocket layers are fully separate and the same scaffold is reimplemented **4 times**:

- LiveHost: `BinanceWebSocketManager` (Infrastructure/Live/Binance) — `SubscribeKline`/`SubscribeAggTrade` + generic `ConnectStream`, behind proper connector abstractions (`ILiveConnector` in Domain; `IMarketDataSource` + `IOrderRouter` in Application). Orders and market-data are separated seams — the role-not-place pattern from LiveHost Plan 2.
- HistoryLoader: `LiquidationStreamService`, `SpotAggTradeStreamService`, `BookTickerStreamService` — each inlines its own `new ClientWebSocket()`, reconnect loop, Binance JSON parsing, and hardcoded Binance URLs. **No connector abstraction at all** — the stream service *is* the connector, smeared across three files.

**Reusable vs not:**
- **Low-level WS transport** (connect + reconnect + frame read) — genuinely shareable; it is the thing duplicated 4×. A single `IWebSocketStream`/reconnect primitive is the right shared unit.
- **High-level connector** — do **not** unify HL and LiveHost. Ingestion (historical completeness, archive backfill, gap detection, monthly partitions, `agg_id` dedup) and live-trading (low latency, order↔fill correlation, session lifecycle) have different requirements and evolve differently. Premature unification couples them.

## 2. The form is Binance-dictated — confirmed

- **Feed taxonomy is Binance-futures vocabulary:** `mark-price`, `funding-rate`, `ls-ratio-global`, `taker-volume`, `open-interest`, `forceOrder`→`liquidations`. Other venues overlap but differ (funding cadence, mark/index semantics; some don't expose long/short ratio at all).
- **CSV schemas are hardcoded per Binance feed** (`o,h,l,c` for mark-price; `side,price,qty,notional_usd` for liquidations).
- **The archive path is a Binance privilege:** `data.binance.vision` monthly zips. Most venues don't publish this, so the whole `IArchiveMaterializer` concept exists only because Binance has an archive. A venue without one means every replenishable feed is effectively always-eager / REST-history.

**Seams that ARE exchange-neutral (phases 2–3a):** `CanonicalSymbol` grammar, `IExchangeSymbology` + `SymbologyRegistry` (per exchange), collection groups with `exchanges: []`, `unsupported` status for venues lacking a symbology, and `ArchiveMaterializerRegistry` keyed by `(exchange, feed)`. This layer is ready — add a file, register it.

## 3. Verticality gap — the actual blockers

The break is between two layers:

| Layer | Vertical-ready? | Why |
|---|---|---|
| Symbology / dispatch (2–3a) | **Yes** | `IExchangeSymbology`, `ArchiveMaterializerRegistry` keyed by `(exchange, feed)` — add file + registration |
| Collection execution (collectors, streams, REST clients, schemas) | **No** | Binance monolith |

Three concrete things that force change to existing services when a second exchange arrives:

1. **`SymbolCollector` holds `FrozenDictionary<string, IFeedCollector>` keyed by `FeedName`, not `(exchange, FeedName)`.** One collector per feed, implicitly Binance — an OKX/Gate `candles` collector would collide on the `"candles"` key.
2. **Stream services are registered by concrete type** (`AddHostedService<SpotAggTradeStreamService>()`), not resolved per-exchange. A second venue means new hosted services, or N services × M exchanges.
3. **`ScheduledCollectorService.CollectCycleAsync` and the REST clients ignore `asset.Exchange`.** The plan carries the exchange (else the tuple is `unsupported`), but collector dispatch picks by feed name only — a resolved non-Binance asset would still route into the Binance collector with Binance URLs.

**Net:** the plan knows the exchange; the execution layer doesn't. That's the missing verticality.

## 4. Direction (recommendation, not a decision)

Extend the `(exchange, feed) → implementation` dispatch that `ArchiveMaterializerRegistry` already models from the archive layer to the whole collection layer:

- **Key `IFeedCollector` by `(exchange, feed)`** (registry, single composition site — same shape as the archive registry and `IExchangeSymbology`). `SymbolCollector` resolves by `(asset.Exchange, feed.FeedName)`.
- **Stream services become per-exchange connectors behind a registry**, resolved by exchange, not registered as Binance-named concrete hosted services.
- **REST clients selected by `asset.Exchange`.**
- **Shared low-level WS transport** extracted (retires the 4× duplication) — but only the transport, connectors stay per-exchange.
- **Feed/schema absence is first-class:** a venue without a given feed (or without an archive) resolves to no materializer / `unsupported`, not a crash. The phase-2/3a `unsupported` + "no `IArchiveMaterializer` → always eager" paths already anticipate this — a second venue exercises them for real.

### Pilot choice: Gate.io + KuCoin

- Both are **Binance-family CEXes** (REST + WS, overlapping feed taxonomy) → good first real second/third adapters: they flush out the `(exchange, feed)` seam without maximal divergence. (A maximally-different venue like Deribit — options greeks/index — would push too many seams at once; take it later.)
- **Neither publishes a Binance-Vision-style monthly archive** (to confirm per venue). So both drive the **archive-optional / REST-history** path — exactly the currently Binance-privileged seam that needs generalizing. This is a feature of the choice, not a problem: it forces the abstraction the archive layer implies but never had to prove.
- **Feed-availability differences to expect:** funding cadence, mark/index-price semantics, long/short-ratio availability (Gate/KuCoin may not expose it the way Binance futures does), liquidation-stream availability. Each maps to the `unsupported`/absent-feed handling — another seam that gets exercised.

### YAGNI framing

The abstraction is only validated by a real second adapter — until then "the right design" is guessing. The good news: phases 2–3a already **reserved** the symbology seam, so this is **filling in, not rewriting**. Best way to fill it: take one Binance-family venue (Gate or KuCoin) as the first real second adapter and let it push the missing execution-layer seams, rather than designing them in the abstract.

## Open questions for the brainstorm

- One collector implementation per `(exchange, feed)`, or a Binance-parameterized base with per-venue overrides where feeds are near-identical (klines) vs distinct implementations where they diverge (funding, liquidations)?
- Where do venue-specific CSV schema differences live — per-`(exchange, feed)` schema, or a normalized canonical schema with per-venue mappers (mirrors the `CanonicalSymbol` idea one level up, at the feed-payload level)?
- Does the shared low-level WS transport live in a new shared project, or in `Storage`/a new `Connectivity` project, given HL and LiveHost must both reference it without referencing each other?
- Archive-optional: is "no archive → always eager, REST-history only" acceptable coverage for Gate/KuCoin, or do we need a generic REST-pagination history backfiller as a materializer variant?
