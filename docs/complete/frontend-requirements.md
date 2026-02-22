# AlgoTradeForge — Frontend Requirements

> **Target**: Next.js 14+ (App Router) with TypeScript, running locally against a REST API backend.
> **Design tone**: Industrial/utilitarian trading terminal — dark theme, data-dense, high contrast. Think Bloomberg Terminal meets modern web. Monospace accents for data, clean sans-serif for UI chrome.

---

## 1. Architecture & Tech Stack

| Layer | Choice |
|---|---|
| Framework | Next.js 14+ with App Router |
| Language | TypeScript (strict) |
| Styling | Tailwind CSS + CSS variables for theming |
| Charts | [Lightweight Charts](https://github.com/nicktomlin/lightweight-charts) by TradingView (candlestick/equity) — or Recharts for equity if simpler |
| Tables | TanStack Table v8 (sorting, dynamic columns, filtering) |
| State | React context + `useState`/`useReducer` for local state; no global store needed yet |
| HTTP | `fetch` wrappers or a thin API client module under `/lib/api/` |
| JSON Editor | `@monaco-editor/react` (for the "Run New" settings panel) |
| Icons | Lucide React |

### Project Structure (suggested)
```
src/
├── app/
│   ├── layout.tsx              # Shell: header + footer
│   ├── page.tsx                # Redirects to /dashboard
│   ├── dashboard/
│   │   └── page.tsx            # Screen 2 — main runs list
│   ├── report/[runId]/
│   │   └── page.tsx            # Screen 1 — single run report
│   └── debug/[runId]/
│       └── page.tsx            # Screen 3 — debug screen
├── components/
│   ├── layout/
│   │   ├── Header.tsx
│   │   ├── Footer.tsx
│   │   └── Sidebar.tsx
│   ├── charts/
│   │   ├── EquityChart.tsx
│   │   ├── CandlestickChart.tsx
│   │   └── TradeMarkers.tsx    # Overlay: entry/exit markers, TP/SL lines
│   ├── tables/
│   │   └── RunsTable.tsx
│   ├── panels/
│   │   ├── MetricsPanel.tsx    # Key-value display for metrics
│   │   ├── ParamsPanel.tsx     # Key-value display for input params
│   │   └── NewRunPanel.tsx     # Slide-over with JSON editor + Run button
│   └── debug/
│       ├── DebugToolbar.tsx
│       └── DebugControls.tsx
├── lib/
│   ├── api/
│   │   ├── client.ts           # Base fetch wrapper, base URL config
│   │   ├── strategies.ts       # GET /strategies
│   │   ├── runs.ts             # GET/POST /runs, GET /runs/:id
│   │   └── debug.ts            # Debug stepping endpoints
│   └── types/
│       └── index.ts            # All shared TypeScript types
└── hooks/
    ├── useStrategies.ts
    ├── useRuns.ts
    └── useDebugSession.ts
```

### API Base URL Config
```ts
// .env.local
NEXT_PUBLIC_API_BASE_URL=http://localhost:5000/api
```

---

## 2. Screen 2 — Dashboard (Main Runs List)

**Route**: `/dashboard`
**Purpose**: Central hub. Select a strategy + run mode, see all historical runs, launch new runs, navigate to reports.

### Layout

```
┌─────────────────────────────────────────────────────┐
│  Header (app name, nav links)                       │
├──────────┬──────────────────────────────────────────┤
│ Sidebar  │  Main Content Area                       │
│          │                                          │
│ Strategy │  [Filters bar]                           │
│ Selector │  ┌─────────────────────────────────┐     │
│ (dropdown│  │  Runs Table                     │     │
│  list)   │  │  (sortable, filterable,         │     │
│          │  │   dynamic metric columns)       │     │
│ ──────── │  │                                 │     │
│ Mode Tabs│  │                                 │     │
│ Backtest │  │                                 │     │
│ Optimize │  └─────────────────────────────────┘     │
│ WalkFwd  │                          [+ Run New] btn │
│ Live     │                                          │
│ Debug    │                                          │
├──────────┴──────────────────────────────────────────┤
│  Footer                                             │
└─────────────────────────────────────────────────────┘
```

### Sidebar

- **Strategy Selector**: Dropdown or scrollable list showing available strategies.
  - `GET /api/strategies` → `{ id, name }[]`
  - Selecting a strategy filters the runs table.
- **Mode Buttons**: Vertical stack of buttons: `Backtest`, `Optimization`, `Walk Forward`, `Live`, `Debug`.
  - Selecting a mode filters the runs table to that run type.
  - Active mode is visually highlighted.
  - `Debug` navigates to `/debug/:runId` (or opens debug setup) instead of filtering.

### Runs Table

- **Columns** (some fixed, some dynamic):
  - Fixed: `Ver` (version), `RunId`, `Asset`, `TF` (timeframe), `Sortino`, `Sharpe`, `Profit Factor`
  - Dynamic: The backend may return additional metric columns — render them dynamically. The API response should include a `columns` array or the table infers columns from the first row's keys.
- **Row actions**:
  - `→` button on each row → navigates to `/report/[runId]` (the Report Screen).
- **Sorting**: Click column headers to sort asc/desc.
- **Filters bar** (above table):
  - Filter by **Run Start Time** (date range picker).
  - Filter by **Period** (the backtest period, e.g. "2024-01-01 to 2024-06-30").
  - These can be simple date inputs for now.

### API Expectations

```
GET /api/strategies
→ [{ "id": "str-a", "name": "Strategy A" }, ...]

GET /api/runs?strategyId=str-a&mode=backtest&startAfter=...&startBefore=...
→ {
    "columns": ["ver", "runId", "asset", "tf", "sortino", "sharpe", "profitFactor", ...dynamicMetrics],
    "rows": [
      { "runId": "abc123", "ver": "1.2", "asset": "BTC-USD", "tf": "1h", "sortino": 1.45, ... },
      ...
    ]
  }
```

### "+ Run New" Button

- Located top-right of the content area.
- **On click**: Opens a **slide-over panel** (from the right) containing:
  1. A **Monaco JSON editor** pre-filled with default settings for the selected strategy+mode.
  2. A **"Run"** button at the bottom of the panel.
- **On Run click**:
  - `POST /api/runs` with body `{ strategyId, mode, settings: <JSON from editor> }`
  - Show loading state.
  - On success: close panel, refresh table, optionally navigate to the new run's report.

---

## 3. Screen 1 — Report Screen

**Route**: `/report/[runId]`
**Purpose**: Detailed view of a single backtest or optimization run result.

### Layout

```
┌─────────────────────────────────────────────────────┐
│  Header + breadcrumb: Dashboard > Strategy A > Run  │
├─────────────────────────────────────────────────────┤
│                                                     │
│  ┌─────────────────────────────────────────────┐    │
│  │  Equity Chart (line chart, full width)       │    │
│  │  X: time, Y: portfolio value                 │    │
│  └─────────────────────────────────────────────┘    │
│                                                     │
│  ┌──────────────────┐  ┌──────────────────────┐     │
│  │ Metrics           │  │ Input Params          │    │
│  │ Sharpe:   1.45    │  │ Period:  2024-01..06  │    │
│  │ Sortino:  1.82    │  │ Asset:   BTC-USD      │    │
│  │ Max DD:   -12.3%  │  │ TF:      1h           │    │
│  │ Win Rate: 64%     │  │ RSI Len: 14           │    │
│  │ ...               │  │ ...                   │    │
│  └──────────────────┘  └──────────────────────┘     │
│                                                     │
│  ┌─────────────────────────────────────────────┐    │
│  │  Interactive Candlestick Chart               │    │
│  │  (shown if candle data is available)         │    │
│  │  - OHLC candles                              │    │
│  │  - Indicator overlays (lines/areas)          │    │
│  │  - Trade markers (entry ▲, exit ▼)           │    │
│  │  - TP/SL horizontal lines per trade          │    │
│  └─────────────────────────────────────────────┘    │
│                                                     │
├─────────────────────────────────────────────────────┤
│  Footer                                             │
└─────────────────────────────────────────────────────┘
```

### Components

#### Equity Chart
- **Library**: TradingView Lightweight Charts (line series) or Recharts AreaChart.
- **Data**: `GET /api/runs/:runId/equity` → `{ time: string, value: number }[]`
- Full width, ~250px height.
- Tooltip on hover showing exact value + date.

#### Metrics Panel
- Key-value pairs in a two-column card.
- **Data**: Part of `GET /api/runs/:runId` response → `metrics: Record<string, string | number>`
- Render dynamically — iterate over whatever keys the backend returns.

#### Input Params Panel
- Same layout as Metrics — key-value card.
- **Data**: Part of `GET /api/runs/:runId` response → `params: Record<string, string | number>`

#### Interactive Candlestick Chart
- **Conditionally rendered**: Only shown if `GET /api/runs/:runId` includes `hasCandleData: true` or equivalent.
- **Library**: TradingView Lightweight Charts (candlestick series + line series for indicators).
- **Data**:
  - `GET /api/runs/:runId/candles` → `{ time, open, high, low, close, volume }[]`
  - `GET /api/runs/:runId/indicators` → `{ name: string, data: { time, value }[] }[]`
  - `GET /api/runs/:runId/trades` → `{ entryTime, entryPrice, exitTime, exitPrice, side, tp?, sl? }[]`
- **Overlays**:
  - Each indicator as a separate line series (different colors, legend).
  - Trade entry/exit as markers (▲ green for long entry, ▼ red for short entry, etc.).
  - TP and SL as horizontal price lines spanning entry→exit time.

### API Expectations

```
GET /api/runs/:runId
→ {
    "runId": "abc123",
    "strategyId": "str-a",
    "strategyName": "Strategy A",
    "mode": "backtest",
    "metrics": { "sharpe": 1.45, "sortino": 1.82, "maxDrawdown": -0.123, "winRate": 0.64, ... },
    "params": { "period": "2024-01-01 to 2024-06-30", "asset": "BTC-USD", "tf": "1h", "rsiLength": 14, ... },
    "hasCandleData": true
  }

GET /api/runs/:runId/equity → [{ "time": "2024-01-01T00:00:00Z", "value": 10000 }, ...]
GET /api/runs/:runId/candles → [{ "time": ..., "open": ..., "high": ..., "low": ..., "close": ... }, ...]
GET /api/runs/:runId/indicators → [{ "name": "SMA_20", "data": [{ "time": ..., "value": ... }] }, ...]
GET /api/runs/:runId/trades → [{ "entryTime": ..., "entryPrice": ..., "exitTime": ..., "exitPrice": ..., "side": "long", "tp": 45000, "sl": 41000 }, ...]
```

---

## 4. Screen 3 — Debug Screen

**Route**: `/debug/[runId]`
**Purpose**: Step through a strategy execution bar-by-bar or trade-by-trade, with playback controls. Like a "debugger" for trading strategies.

### Layout

```
┌──────────────────────────────────────────────────────────────┐
│  Debug Toolbar                                                │
│  [To Next Bar] [To Next Trade] [To DATE: [____] [____]]      │
│                                           date     time       │
│  [▶ Play] [⏸ Pause] [⏹ Stop]                                │
├──────────────────────────────────────────────────────────────┤
│                                                              │
│  ┌──────────────────────────────────────────────────────┐    │
│  │  Interactive Candlestick Chart                        │    │
│  │  - Candles up to current debug point                  │    │
│  │  - All indicator overlays                             │    │
│  │  - Trade markers + TP/SL lines for visible trades     │    │
│  │  - "Main data subscription" = the primary asset       │    │
│  └──────────────────────────────────────────────────────┘    │
│                                                              │
│  ┌──────────────────────┐  ┌───────────────────────────┐     │
│  │ Metrics (live)        │  │ Params                     │    │
│  │ Updated at each step  │  │ Static input params        │    │
│  └──────────────────────┘  └───────────────────────────┘     │
│                                                              │
└──────────────────────────────────────────────────────────────┘
```

### Debug Toolbar

A fixed/sticky bar at the top of the debug view:

| Control | Behavior |
|---|---|
| **To Next Bar** | Advances simulation by one candle. `POST /api/debug/:sessionId/step?type=bar` |
| **To Next Trade** | Advances simulation to the next trade event. `POST /api/debug/:sessionId/step?type=trade` |
| **To Date** | Two inputs: date (`yyyy-MM-dd`) + time (`HH:mm`). Advances simulation to that point. `POST /api/debug/:sessionId/step?type=date&target=2026-11-23T11:05:00` |
| **▶ Play** | Auto-steps bar-by-bar at an interval (~500ms). Uses `setInterval` calling the step endpoint. |
| **⏸ Pause** | Stops auto-play. |
| **⏹ Stop** | Ends debug session. `DELETE /api/debug/:sessionId`. Navigates back to dashboard. |

### Debug Chart
- Same candlestick chart component as the Report Screen, but:
  - Only renders candles **up to the current debug timestamp**.
  - Refreshes after each step (the step response returns updated chart data, or the chart re-fetches).
  - Shows indicators computed up to current point.
  - Shows trades that have occurred so far (with OPEN label for active positions, TP/SL lines).

### Debug Metrics & Params
- Same `MetricsPanel` and `ParamsPanel` components.
- **Metrics update** after each step (running P&L, current position, etc.).
- **Params are static** (the strategy's input configuration).

### API Expectations

```
POST /api/debug/start
  Body: { "strategyId": "str-a", "settings": { ... } }
→ { "sessionId": "dbg-001", "currentTime": "2024-01-01T00:00:00Z" }

POST /api/debug/:sessionId/step
  Body: { "type": "bar" | "trade" | "date", "target?": "2024-03-15T11:05:00Z" }
→ {
    "currentTime": "2024-01-01T01:00:00Z",
    "candles": [...],          // all candles up to currentTime
    "indicators": [...],       // all indicator data up to currentTime
    "trades": [...],           // all trades so far
    "metrics": { ... },        // running metrics at this point
    "openPosition": { "side": "long", "entryPrice": 42500, "tp": 45000, "sl": 41000 } | null
  }

DELETE /api/debug/:sessionId
→ 204 No Content
```

---

## 5. Shared Components & Patterns

### Header
- App name/logo on the left: **"AlgoTradeForge"**
- Nav links: `Dashboard`, `Docs` (placeholder), `Settings` (placeholder)

### Footer
- Minimal: version number, links to docs/GitHub (placeholder)

### MetricsPanel / ParamsPanel
- Reusable component: takes `title: string` and `data: Record<string, string | number>`.
- Renders as a card with the title and a list of key-value rows.
- Keys shown as labels (formatted: `camelCase` → `Camel Case`), values right-aligned or in a second column.

### CandlestickChart (shared)
- Wrapper around TradingView Lightweight Charts.
- Props:
  - `candles: OHLC[]`
  - `indicators?: { name: string, color?: string, data: { time, value }[] }[]`
  - `trades?: Trade[]`
  - `height?: number`
- Handles: creating chart instance, adding series, cleanup on unmount.
- Trade markers rendered as markers on the candlestick series.
- TP/SL rendered as horizontal price lines.

### Loading & Error States
- Skeleton loaders for charts and tables while data loads.
- Error boundary with retry button if API calls fail.
- Toast notifications for actions (run started, run failed, etc.).

### Responsive Behavior
- Primary target: desktop (1280px+). This is a power-user tool.
- Sidebar collapses to icons on smaller screens.
- Tables get horizontal scroll on narrow viewports.

---

## 6. Mock Data / Development Mode

Since the backend doesn't exist yet, include a **mock mode** toggled by an env variable:

```env
NEXT_PUBLIC_USE_MOCKS=true
```

When enabled, API calls return realistic fake data (hardcoded JSON fixtures in `/lib/mocks/`). This allows full frontend development without a running backend. Mock data should include:

- 2 strategies ("Strategy A", "Strategy B")
- ~10 sample runs with varied metrics
- Sample equity curve (100 data points)
- Sample candle data (200 candles with OHLC)
- 2-3 sample indicators (SMA, RSI)
- 5-6 sample trades with TP/SL

---

## 7. Summary of API Endpoints (Contract)

| Method | Endpoint | Purpose |
|---|---|---|
| `GET` | `/api/strategies` | List all strategies |
| `GET` | `/api/runs?strategyId=&mode=&startAfter=&startBefore=` | List runs (filtered) |
| `POST` | `/api/runs` | Start a new run |
| `GET` | `/api/runs/:runId` | Run details (metrics, params, flags) |
| `GET` | `/api/runs/:runId/equity` | Equity curve data |
| `GET` | `/api/runs/:runId/candles` | OHLC candle data |
| `GET` | `/api/runs/:runId/indicators` | Indicator overlay data |
| `GET` | `/api/runs/:runId/trades` | Trade list with entries/exits |
| `POST` | `/api/debug/start` | Start debug session |
| `POST` | `/api/debug/:sessionId/step` | Advance debug session |
| `DELETE` | `/api/debug/:sessionId` | End debug session |