# Feature Specification: Trading Frontend

**Feature Branch**: `008-trading-frontend`
**Created**: 2026-02-19
**Updated**: 2026-02-22
**Status**: Draft
**Input**: User description: "Next.js trading frontend with dashboard, report, and debug screens for viewing backtest results, launching runs, and stepping through strategy execution. Charts use TradingView Lightweight Charts. Debug data comes from a JSONL event stream via WebSocket."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Step Through Strategy Execution in Debug Mode (Priority: P1)

A trader navigates to the debug screen and enters session parameters (asset, exchange, strategy, date range, initial cash, commission, slippage, timeframe, strategy parameters) in a JSON editor, then clicks a submit button to start the session. The frontend creates the session via the backend REST API, which initializes a backtest in paused state. The frontend then opens a WebSocket connection to the session. The trader uses toolbar controls to step through execution: advance one event, advance one bar, advance to the next trade event, advance to the next signal, jump to a specific timestamp or sequence number, or auto-play. Each step sends a JSON command over WebSocket; the backend advances the simulation and streams back JSONL events followed by a snapshot summary. The candlestick chart grows incrementally as new bar and indicator events arrive. The trader can toggle mutation event streaming on/off via a `set_export` control.

**Why this priority**: The visual debugger is the most novel and high-value feature of the frontend — it enables traders to understand exactly what their strategy did and why at each point in time. It drives the core debugging workflow and is the primary reason for building the frontend.

**Independent Test**: Can be fully tested by starting a debug session, stepping forward one bar, verifying a `bar` event arrives via WebSocket and the chart adds one candle with updated indicators, then stepping to the next trade and verifying order lifecycle events render as trade markers.

**Acceptance Scenarios**:

1. **Given** the user is on the debug screen, **When** the page loads, **Then** a JSON editor is displayed pre-filled with a default session configuration template and a "Start" submit button.
2. **Given** the user has edited the JSON and clicks "Start", **When** the session is created successfully, **Then** a debug session is created via the REST API (returning a session ID), a WebSocket connection is established to that session, the backend begins in paused state, the JSON editor is hidden, and the debug screen displays the toolbar, an empty chart, and initial metrics/params panels.
3. **Given** a debug session is active and paused, **When** the user clicks "Next" (single step), **Then** a `next` WebSocket command is sent, the backend advances by one event, streams the event and a snapshot summary, and the UI updates accordingly.
4. **Given** a debug session is active and paused, **When** the user clicks "To Next Bar", **Then** a `next_bar` WebSocket command is sent, the backend advances to the next `bar` event, and all intermediate events plus the snapshot are rendered (new candle on chart, updated indicator values, refreshed metrics).
5. **Given** a debug session is active and paused, **When** the user clicks "To Next Trade", **Then** a `next_trade` WebSocket command is sent, the backend advances to the next order lifecycle event, and all intermediate events (bars, indicators) are rendered along the way plus the trade marker.
6. **Given** a debug session is active and paused, **When** the user clicks "To Next Signal", **Then** a `next_signal` WebSocket command is sent, the backend advances to the next signal event, and all intermediate events are rendered.
7. **Given** a debug session is active, **When** the user enters a target timestamp and clicks "Run To Timestamp", **Then** a `run_to_timestamp` WebSocket command is sent with the `timestampMs` field, and the backend streams all events up to that point, which are progressively rendered.
8. **Given** a debug session is active, **When** the user enters a target sequence number and clicks "Run To Sequence", **Then** a `run_to_sequence` WebSocket command is sent with the `sequenceNumber` field, and the backend streams all events up to that sequence number.
9. **Given** a debug session is active, **When** the user clicks "Play", **Then** a `continue` command is sent and incoming events are rendered in real-time as they stream in; clicking "Pause" sends a `pause` command and the simulation halts after the current event.
10. **Given** a debug session is active, **When** the user clicks "Stop", **Then** the WebSocket connection is closed, the debug session is terminated via the REST DELETE endpoint, and the user is navigated back to the dashboard.
11. **Given** events are streaming during a debug session, **When** a `bar` event arrives, **Then** it is appended as a new candle on the chart; when an `ind` event arrives, the corresponding indicator line is extended; when `ord.place` events arrive, order placement markers are drawn; when `ord.fill` or `pos` events arrive, trade fill markers are drawn.
12. **Given** there is an open position (a `pos` event with an active position), **When** the chart renders, **Then** the open position is visually indicated with an entry marker and current position quantity/side. TP/SL line rendering on the debug chart is deferred to a future iteration.
13. **Given** a debug session is active, **When** the server sends a snapshot message (type: `snapshot`), **Then** the running metrics panel updates with the snapshot data (session active state, sequence number, timestamp, portfolio equity, fills this bar).
14. **Given** a debug session is active, **When** the server sends an error message (type: `error`), **Then** an error notification is displayed to the user with the error text.

---

### User Story 2 - View Detailed Run Report (Priority: P2)

A trader navigates to a backtest run's report screen and sees a full equity curve chart, a metrics summary (Sharpe, Sortino, max drawdown, win rate, etc.), the input parameters used for the run, and—if candle data is available (indicated by the `hasCandleData` flag)—an interactive candlestick chart with indicator overlays and trade entry/exit markers with TP/SL lines. Equity data is fetched from a dedicated endpoint. Metrics and parameters come from the run detail response. All charts use TradingView Lightweight Charts. For optimization runs, the trader sees an optimization summary with the trials table, and can drill into any trial's individual report.

**Why this priority**: The report screen delivers the core analytical value—understanding how a strategy performed. It is the natural companion to the debug screen and reuses the same chart components.

**Independent Test**: Can be fully tested by navigating to a run report URL and verifying the equity chart, metrics panel, params panel, and candlestick chart (with overlays) all render correctly with the run's data.

**Acceptance Scenarios**:

1. **Given** the user navigates to a backtest run report, **When** the page loads, **Then** an equity curve chart (TradingView Lightweight Charts line series) is displayed using data from the equity endpoint (timestamp + value pairs), with crosshair tooltips.
2. **Given** the report page is loaded, **When** metrics data is available, **Then** a metrics panel dynamically displays all key-value pairs from the run's metrics dictionary (totalTrades, sharpeRatio, sortinoRatio, maxDrawdownPct, winRatePct, profitFactor, netProfit, finalEquity, etc.).
3. **Given** the report page is loaded, **When** params data is available, **Then** an input params panel dynamically displays all strategy configuration key-value pairs from the run's parameters dictionary.
4. **Given** the run has candle data available (`hasCandleData` is true), **When** the report page loads, **Then** an interactive candlestick chart (TradingView Lightweight Charts candlestick series) is displayed with OHLC candles parsed from the run's event data.
5. **Given** the candlestick chart is rendered, **When** indicator data is available, **Then** each indicator is overlaid as a separate colored line series with a legend.
6. **Given** the candlestick chart is rendered, **When** trade data is available, **Then** entry and exit markers are displayed on the chart, and TP/SL horizontal price lines span each trade's duration.
7. **Given** the run has no candle data (`hasCandleData` is false), **When** the report loads, **Then** the candlestick chart section is omitted entirely; the remaining panels (equity chart, metrics, params) expand to fill the available space.
8. **Given** the user navigates to an optimization run report, **When** the page loads, **Then** an optimization summary is displayed (total combinations, duration, sort metric) along with a trials table showing each trial's parameters and metrics, sorted by the optimization's sort criterion.
9. **Given** the optimization trials table is displayed, **When** the user clicks a trial row, **Then** they are taken to the individual backtest report for that trial (which has its own equity curve, metrics, and params).

---

### User Story 3 - Browse and Filter Historical Runs (Priority: P3)

A trader opens the dashboard, selects a strategy from a list of strategy names, and selects a run mode tab (Backtest or Optimization). The corresponding table loads with paged results filtered by strategy. The trader can further filter by asset, exchange, timeframe, and date range (data start/end). They sort by Sortino ratio to find top performers. They click a row to navigate to the detailed report for that run. Optimization runs show in their own tab with summary-level information and a trial count.

**Why this priority**: The runs table is the central hub for navigating between runs. The backend API for listing runs and optimizations is available, with paging and filtering support.

**Independent Test**: Can be fully tested by loading the dashboard, selecting a strategy, and verifying the table populates with sortable/filterable run data from the backend.

**Acceptance Scenarios**:

1. **Given** the user is on the dashboard, **When** it loads, **Then** the strategy selector is populated with strategy names fetched from the backend (a list of strategy name strings).
2. **Given** the user is on the dashboard, **When** they select a strategy from the sidebar, **Then** the runs table updates to show only runs for that strategy.
3. **Given** a strategy is selected, **When** the user selects the "Backtest" mode tab, **Then** backtest runs appear in the table, fetched with paging support (limit/offset).
4. **Given** a strategy is selected, **When** the user selects the "Optimization" mode tab, **Then** optimization runs appear in the table with summary information (total combinations, sort metric, duration) and a drill-down to view trials.
5. **Given** runs are displayed, **When** the user sets filters (asset name, exchange, timeframe, date range), **Then** the table re-fetches data with the corresponding query parameters applied.
6. **Given** runs are displayed with dynamic metric columns from the backend, **When** the table renders, **Then** all metric columns are shown including any beyond the fixed set (StrategyVersion, RunId, Asset, Exchange, TF, Sortino, Sharpe, Profit Factor, etc.).
7. **Given** more runs exist than the current page size, **When** the user scrolls or clicks next page, **Then** the next page of results is loaded using offset-based pagination.
8. **Given** a run row is visible, **When** the user clicks the navigate button on that row, **Then** they are taken to the report screen for that run.

---

### User Story 4 - Launch a New Run (Priority: P4)

A trader clicks the "+ Run New" button on the dashboard. Depending on the currently selected mode tab (Backtest or Optimization), a slide-over panel opens with a form or JSON editor pre-filled with defaults. For backtests, the form includes asset, exchange, strategy, date range, initial cash, commission, slippage, timeframe, and strategy parameters. For optimizations, the form additionally includes optimization axes (range, fixed, discrete set, or module choice overrides), data subscriptions, max parallelism, max combinations, and sort metric. The trader edits settings and clicks "Run" to submit. The system shows a loading state, and on success the table refreshes with the new run.

**Why this priority**: Launching runs from the UI is a convenience feature. Traders can launch runs via the CLI or API directly. This is a lower priority than viewing and debugging results.

**Independent Test**: Can be fully tested by clicking "+ Run New", editing the JSON, submitting, and verifying a loading state appears followed by the table refreshing with the new run entry.

**Acceptance Scenarios**:

1. **Given** the user is on the dashboard with a strategy selected, **When** they click "+ Run New", **Then** a slide-over panel opens from the right with a form/JSON editor appropriate for the current mode (Backtest or Optimization) and a "Run" button.
2. **Given** the slide-over is open in Backtest mode, **When** it renders, **Then** the editor is pre-filled with default backtest settings (asset, exchange, strategy, date range, initial cash, commission, slippage, timeframe, strategy parameters).
3. **Given** the slide-over is open in Optimization mode, **When** it renders, **Then** the editor is pre-filled with default optimization settings including parameter axes configuration, data subscriptions, parallelism, max combinations, and sort metric.
4. **Given** the user has edited settings, **When** they click "Run", **Then** a loading state is shown and the request is submitted to the appropriate backend endpoint (backtests or optimizations).
5. **Given** the run request succeeds, **When** the response returns, **Then** the slide-over closes and the runs table refreshes to include the new run.
6. **Given** the run request fails, **When** the error response returns, **Then** an error notification is displayed to the user and the slide-over remains open for correction.

---

### User Story 5 - Frontend Development Without Backend (Priority: P2)

A frontend developer enables mock mode and can fully develop, test, and demonstrate the debug and report screens using realistic sample data without a running backend. For the debug screen, mock mode replays a pre-recorded sequence of JSONL events to simulate WebSocket streaming (including snapshot messages). For the report screen, mock data includes equity curves, candle data, indicators, and trades. For the dashboard, mock data includes paged lists of backtest and optimization runs.

**Why this priority**: Mock mode is critical for parallel frontend/backend development. The debug screen (P1) and report screen (P2) need mock data from the start to enable development before the backend event system and WebSocket server are implemented.

**Independent Test**: Can be fully tested by enabling the mock mode setting, navigating through all screens, and verifying realistic data appears everywhere without any network calls to a real backend or WebSocket server.

**Acceptance Scenarios**:

1. **Given** mock mode is enabled, **When** the user starts and steps through a debug session, **Then** each step returns progressive sample events and snapshot responses via a simulated WebSocket (or mock event replay).
2. **Given** mock mode is enabled, **When** the user navigates to a run report, **Then** equity charts, metrics, params, candlestick charts, indicators, and trade markers all render with sample data.
3. **Given** mock mode is enabled, **When** the dashboard loads, **Then** the strategies list and runs table (both backtest and optimization tabs) populate with realistic sample data including paged responses.
4. **Given** mock mode is disabled and the backend is unreachable, **When** any screen loads, **Then** an error state is displayed with a retry option (not mock data).

---

### Edge Cases

- What happens when the backend returns an empty runs list for a strategy/mode combination? The table displays an empty state message (e.g., "No runs found").
- What happens when a run report has no indicator data? The candlestick chart renders without indicator overlays.
- What happens when a run report has no trade data? The candlestick chart renders without trade markers or TP/SL lines.
- What happens when the JSON in the "Run New" editor is invalid? The "Run" button is disabled or a validation error is shown before submission.
- What happens when the WebSocket connection drops during a debug session? An error notification is displayed with a reconnect option or the session is terminated gracefully.
- What happens when a debug step command fails or times out? The server sends an error message (type: `error`) over WebSocket, and the frontend displays the error text to the user. The user can retry the step or stop the session.
- What happens when the user navigates away from an active debug session? The WebSocket connection is closed and the session is terminated via the DELETE endpoint (with a confirmation prompt if the session is in progress).
- What happens when metric keys from the backend contain unexpected formats? Keys are displayed as-is with camelCase-to-Title-Case formatting applied best-effort.
- What happens on screens narrower than the primary desktop target? The sidebar collapses to icons and tables gain horizontal scrolling.
- What happens when events arrive faster than the chart can render during auto-play? Events are buffered and rendered in batches to maintain UI responsiveness.
- What happens when a WebSocket client is already connected to a debug session? The server rejects the second connection with a 409 Conflict.
- What happens when an optimization run has no trials? The trials table shows an empty state.
- What happens when the user reaches the last page of paged results? The pagination control disables the "next" action (guided by the `hasMore` flag in the paged response).

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: System MUST display a sidebar with a strategy selector that fetches and lists all available strategy names from the backend (returned as a list of strings).
- **FR-002**: System MUST display mode selection tabs (Backtest, Optimization) in the sidebar or above the runs table; selecting a mode fetches data from the corresponding backend endpoint with paging support.
- **FR-003**: System MUST render a runs table for backtests with columns including StrategyVersion, RunId, Asset, Exchange, TF, Sortino, Sharpe, Profit Factor, and dynamically render additional metric columns from the run's metrics dictionary. Runs are displayed in the order returned by the backend (descending completion date). Client-side sorting is deferred to a future iteration.
- **FR-004**: System MUST provide filters above the runs table for strategyName, assetName, exchange, timeFrame, and date range (from/to), all passed as query parameters to the backend. The Backtest tab MUST pass `standaloneOnly=true` by default to exclude optimization trial runs from the listing.
- **FR-005**: System MUST provide a navigate action on each table row that takes the user to the detailed report screen for that run.
- **FR-006**: System MUST provide a "+ Run New" button that opens a slide-over panel with a mode-aware form/JSON editor (different fields for backtest vs optimization) pre-filled with default settings and a "Run" submit button.
- **FR-007**: System MUST display an equity curve chart (line series) on the report screen, using timestampMs/value pairs from the equity endpoint, with crosshair tooltips.
- **FR-008**: System MUST display a metrics panel on the report screen that dynamically renders all key-value metric pairs from the run's metrics dictionary (camelCase keys formatted as Title Case labels).
- **FR-009**: System MUST display an input params panel on the report screen that dynamically renders all strategy configuration key-value pairs from the run's parameters dictionary.
- **FR-010**: System MUST conditionally display an interactive candlestick chart on the report screen when `hasCandleData` is true, fetching structured candle/indicator/trade data from the backend events endpoint, showing OHLC candles, indicator overlays (as colored line series with legend), trade entry/exit markers, and TP/SL horizontal price lines.
- **FR-011**: System MUST provide a debug toolbar with controls: "Next" (sends `next`), "To Next Bar" (sends `next_bar`), "To Next Trade" (sends `next_trade`), "To Next Signal" (sends `next_signal`), "Run To Timestamp" (sends `run_to_timestamp` with `timestampMs`), "Run To Sequence" (sends `run_to_sequence` with `sequenceNumber`), "Play" (sends `continue`), "Pause" (sends `pause`), and "Stop" (terminates session via DELETE endpoint).
- **FR-012**: System MUST establish a WebSocket connection for debug sessions and process incoming messages: JSONL events (`bar`, `ind`, `ord.*`, `pos`, `sig`, `risk`, `run.*`) to progressively build the chart, `snapshot` messages to update running metrics, and `error` messages to display error notifications. Events without a dedicated chart visualization (`risk`, `warn`, `err`, `ord.cancel`, `ord.reject`, `run.start`, `run.end`) MUST still be counted in the sequence and reflected in the DebugMetrics panel but have no chart rendering.
- **FR-013**: System MUST progressively render candles from `bar` events, extend indicator lines from `ind` events, place order markers from `ord.place` events, and place trade fill markers from `ord.fill` events and position markers from `pos` events on the debug candlestick chart. TP/SL horizontal line rendering on the debug chart is deferred to a future iteration.
- **FR-014**: System MUST update running metrics in the debug screen from `snapshot` messages (session active state, sequence number, timestamp, portfolio equity, fills this bar, subscription index).
- **FR-015**: System MUST support a mock data mode (toggled by configuration) that returns realistic sample data for all screens—including simulated WebSocket event and snapshot streaming for debug—without requiring a running backend.
- **FR-016**: System MUST display loading skeleton states for charts and tables while data is being fetched.
- **FR-017**: System MUST display error states with retry options when backend requests or WebSocket connections fail.
- **FR-018**: System MUST display toast notifications for user-initiated actions (run started, run failed, debug session ended, etc.).
- **FR-019**: System MUST format camelCase metric and param keys as human-readable Title Case labels.
- **FR-020**: System MUST provide a shared, reusable application shell with a header (app name, navigation links) and footer (version, placeholder links).
- **FR-021**: System MUST use a dark-themed, data-dense visual design optimized for desktop displays (1280px+ primary target) with graceful degradation on narrower screens (collapsible sidebar, horizontally scrollable tables).
- **FR-022**: System MUST use TradingView Lightweight Charts for all chart rendering (equity curves, candlestick charts in both report and debug screens).
- **FR-023**: System MUST support offset-based pagination for both backtest and optimization run lists, using the `limit`, `offset`, `totalCount`, and `hasMore` fields from the paged response.
- **FR-024**: System MUST display an optimization report view showing optimization summary (total combinations, duration, sort metric) and a trials table with each trial's parameters and performance metrics, with the ability to navigate to an individual trial's backtest report.
- **FR-025**: System MUST create debug sessions via the REST API (POST) before establishing the WebSocket connection, and terminate sessions via the REST API (DELETE) when stopping.
- **FR-026**: System MUST provide a `set_export` toggle control in the debug toolbar that sends a `set_export` WebSocket command with a `mutations` boolean to enable/disable mutation event streaming (e.g., `bar.mut`, `ind.mut`).
- **FR-027**: System MUST display a JSON editor with a "Start" button on the debug screen for entering session configuration parameters (asset, exchange, strategy, date range, initial cash, commission, slippage, timeframe, strategy parameters) before a debug session is active. The editor is hidden once the session starts.

### Key Entities

- **Strategy**: A named trading strategy available in the system. The backend returns a flat list of strategy name strings. Strategies are the top-level grouping for all runs.
- **Backtest Run**: A single backtest execution of a strategy. Has a unique ID, strategy name, strategy version, input parameters (dynamic dictionary), asset, exchange, timeframe, initial cash, commission, slippage, timing data (startedAt, completedAt, dataStart, dataEnd, durationMs), total bars, performance metrics (dynamic dictionary including totalTrades, sharpeRatio, sortinoRatio, maxDrawdownPct, winRatePct, profitFactor, netProfit, finalEquity, etc.), a `hasCandleData` flag, a `runMode` indicator, and an optional `optimizationRunId` linking it to a parent optimization.
- **Optimization Run**: A parameter optimization execution that produced multiple trials. Has a unique ID, strategy name/version, timing data, total combinations, sort metric, data range, initial cash, commission, slippage, max parallelism, asset/exchange/timeframe, and a list of trial backtest runs.
- **Equity Point**: A single data point on the equity curve with `timestampMs` (epoch milliseconds) and `value` (decimal portfolio value). Fetched via a dedicated endpoint for a given backtest run.
- **Candle**: An OHLC price bar for a given time interval, extracted from `bar` events in the run's event data.
- **Indicator**: A named computed time series (e.g., SMA, RSI) extracted from `ind` events. Each indicator has a name and a series of time-value data points.
- **Trade**: A completed or open trading action with entry time/price, exit time/price, side (long/short), and optional take-profit and stop-loss levels, extracted from order lifecycle events (`ord.fill`, `pos`).
- **Debug Session**: A stateful, interactive stepping session. Created via REST POST (returns sessionId, assetName, strategyName, createdAt). Connected via WebSocket for bidirectional communication. The frontend sends JSON commands (`next`, `next_bar`, `next_trade`, `next_signal`, `run_to_timestamp`, `run_to_sequence`, `continue`, `pause`, `set_export`) and receives JSONL events, snapshot summaries, error messages, and set_export acknowledgements. Session status can be polled via REST GET. Session is terminated via REST DELETE.
- **Debug Snapshot**: A summary message (type: `snapshot`) sent by the server after each command execution, containing: `sessionActive`, `sequenceNumber`, `timestampMs`, `subscriptionIndex`, `isExportableSubscription`, `fillsThisBar`, `portfolioEquity`.
- **Backtest Event**: A JSON object following the canonical event envelope (`ts`, `sq`, `_t`, `src`, `d`) as defined in the event model. Events include market data (`bar`, `bar.mut`), indicators (`ind`, `ind.mut`), signals (`sig`), risk checks (`risk`), order lifecycle (`ord.place`, `ord.fill`, `ord.cancel`, `ord.reject`, `pos`), and system events (`run.start`, `run.end`, `err`, `warn`).

## Clarifications

### Session 2026-02-21

- Q: How should the frontend obtain candle/indicator/trade event data for completed run reports (no REST endpoint exists)? → A: Backend adds a dedicated structured events endpoint (e.g., GET `/api/backtests/{id}/events`) returning parsed candles, indicators, and trades as JSON.
- Q: How does a user initiate a debug session (what UI for entering parameters)? → A: Dedicated JSON editor + "Start" button on the debug screen itself (no structured form, just raw JSON and submit).
- Q: How should table sorting work with paged data (backend has no sort param)? → A: No client-side sorting for now. Backend returns results sorted by descending completion date (`completed_at DESC`). Sorting deferred to future iteration.

## Assumptions

- The backend exposes the following REST API endpoint groups, all documented via OpenAPI/Swagger:
  - **Strategies** (`/api/strategies`): GET returns a list of strategy name strings.
  - **Backtests** (`/api/backtests`): POST to run, GET to list (paged, filterable), GET by ID for detail, GET `/{id}/equity` for equity curve.
  - **Optimizations** (`/api/optimizations`): POST to run, GET to list (paged, filterable), GET by ID for detail (includes trials array).
  - **Debug Sessions** (`/api/debug-sessions`): POST to create, GET by ID for status, DELETE to terminate. All debug endpoints are localhost-only.
  - **Debug WebSocket** (`/api/debug-sessions/{id}/ws`): Bidirectional WebSocket for event streaming and command control.
- List endpoints return paged responses with `items`, `totalCount`, `limit`, `offset`, and `hasMore` fields. Default page size is 50.
- List endpoints accept common filter query parameters: `strategyName`, `assetName`, `exchange`, `timeFrame`, `from`, `to`, plus `limit` and `offset` for paging. Backtest list additionally supports `standaloneOnly` to exclude optimization trials. Results are returned sorted by descending completion date (`completed_at DESC`, most recent first).
- The backend `next_type` debug command is available but intentionally not exposed in the frontend toolbar (YAGNI per Principle VI). It can be added in a future iteration if needed.
- The WebSocket protocol uses JSON for client-to-server commands (with a `command` field and optional `sequenceNumber`, `timestampMs`, or `mutations` fields) and streams raw JSONL events plus typed JSON messages (`snapshot`, `error`, `set_export_ack`) from server to client. (The backend also supports a `_t` field for the `next_type` command, which is intentionally excluded from the frontend per Principle VI.)
- Candle/indicator/trade data for the report candlestick chart is fetched from a dedicated backend endpoint (e.g., GET `/api/backtests/{id}/events`) that returns parsed, structured JSON (candles, indicators, trades) extracted from the run's `events.jsonl` file. This endpoint needs to be added to the backend as a prerequisite for FR-010. Available only when `hasCandleData` is true.
- Optimization trials are backtest runs with `optimizationRunId` set. Optimization trials do NOT have `hasCandleData` (they have no event files).
- The application is a single-user, locally-run tool (no authentication or multi-tenancy required).
- The "Docs" and "Settings" navigation links in the header are placeholders for future development and link to stub pages or are inert.
- The auto-play feature in debug mode sends a `continue` WebSocket command, and the backend streams events continuously until a `pause` command is received. The frontend renders events as they arrive in real-time.
- Mock data fixtures are static JSON files bundled with the frontend; for the debug screen, mock mode replays a pre-recorded sequence of JSONL events and snapshot messages to simulate WebSocket streaming.
- The default route (`/`) redirects to `/dashboard`.
- The frontend parses the compact event envelope fields (`ts`, `sq`, `_t`, `src`, `d`) as defined in the event model. Event-type-specific data is in the `d` field.
- Only events from exportable `DataSubscription`s produce `bar` and `ind` events. The frontend does not need to filter subscriptions—it renders all bar/indicator events it receives. Mutation events (`bar.mut`, `ind.mut`) are only received when enabled via the `set_export` control.
- Only two run modes (Backtest, Optimization) are currently supported by the backend API. Walk Forward and Live modes are reserved for future development. Debug is a separate interactive workflow, not a persisted run mode.
- **TP/SL on debug chart deferred**: The debug screen renders order placement markers (`ord.place`), trade fill markers (`ord.fill`), and position markers (`pos`) but does NOT render TP/SL horizontal price lines. Deriving TP/SL from the progressive event stream requires complex stateful logic (tracking pending orders, matching to positions) that is deferred to a future iteration. The report screen retains full TP/SL rendering because the backend events endpoint provides pre-parsed `TradeData` with explicit `takeProfitPrice`/`stopLossPrice` fields.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A user can navigate from the dashboard to any run's report screen in 2 clicks or fewer (select strategy, click run row).
- **SC-002**: All three primary screens (Dashboard, Report, Debug) are fully functional and navigable using only mock data, without a running backend.
- **SC-003**: The runs table supports filtering by strategy, asset, exchange, timeframe, and date range, with results updating in under 1 second for datasets of up to 500 runs. Runs are displayed in descending completion-date order.
- **SC-004**: The report screen renders the equity chart, metrics, params, and candlestick chart (with indicators and trade markers) for a run with 200 candles and 5 trades in under 2 seconds.
- **SC-005**: A user can launch a new run from the dashboard (open panel, edit settings, submit) in under 30 seconds.
- **SC-006**: The debug screen correctly processes a WebSocket step command and updates the chart with the new bar/indicator/trade data within 1 second.
- **SC-007**: All screens display appropriate loading skeletons during data fetch and error states with retry on failure; no blank or broken screens are shown to the user.
- **SC-008**: The application is fully usable on desktop screens 1280px wide and above; on narrower screens, the sidebar collapses and tables scroll horizontally without layout breakage.
- **SC-009**: The optimization report correctly displays the trials table with all trial parameters and metrics, and allows navigation to individual trial reports.
