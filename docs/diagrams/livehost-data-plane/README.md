# LiveHost Data Plane — Data-Flow Diagrams

Mermaid (`.mmd`) diagrams of the as-built LiveHost data plane (Plan 4). Render with any
Mermaid viewer (VS Code Mermaid extension, `mmdc`, mermaid.live, GitHub).

| File | Shows |
|------|-------|
| `01-component-map.mmd` | Component & role map — ingest → {archival (lossless), dispatch (best-effort)} → per-session queues → strategy → orders |
| `02-tradetick-flow.mmd` | `TradeTick`: one stream fanning to a tick strategy (`OnTradeTick`) **and** an alt-bar (`OnBarComplete`) |
| `03-timebar-flow.mmd` | `TimeBar`: venue-published via kline WS — `OnBarStart` (new open-time) + `OnBarComplete` (close) |
| `04-altbar-flow.mmd` | `AltBar`: tick-aggregated via the shared engine, frozen threshold, `OnBarComplete` only |
| `05-quotetick-flow.mmd` | `QuoteTick`: archival only today (strategy path = Framework v2) |

**Invariants the diagrams encode:**
- Archival is lossless and always runs; dispatch is best-effort (`MarketDataQueue`, DropNewest).
- Fills ride a separate `EventQueue` (Wait) so market data can never drop or starve them.
- One single per-session reader serializes all strategy callbacks.
- Trade-derived inputs fan from one shared tick stream; time bars are venue-published and bypass the router/accumulator; quotes are state (no bar semantics) and only archive.
