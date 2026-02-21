# Feature Specification: Trading Frontend

**Feature Branch**: `006-trading-frontend`
**Created**: 2026-02-19
**Status**: Draft
**Input**: User description: "Next.js trading frontend with dashboard, report, and debug screens for viewing backtest results, launching runs, and stepping through strategy execution. Charts use TradingView Lightweight Charts. Debug data comes from a JSONL event stream via WebSocket."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Step Through Strategy Execution in Debug Mode (Priority: P1)

A trader starts a debug session for a strategy. The backend begins a backtest run in paused state and opens a WebSocket connection. The frontend receives events from a JSONL event stream via WebSocket and renders candles, indicators, trade markers, and running metrics progressively. The trader uses toolbar controls to step through execution: advance one bar, advance to the next trade event, jump to a specific date/time, or auto-play. Each step sends a WebSocket control command; the backend advances the simulation and streams back the resulting events. The candlestick chart grows incrementally as new bar and indicator events arrive.

**Why this priority**: The visual debugger is the most novel and high-value feature of the frontend — it enables traders to understand exactly what their strategy did and why at each point in time. It drives the core debugging workflow and is the primary reason for building the frontend.

**Independent Test**: Can be fully tested by starting a debug session, stepping forward one bar, verifying a `bar` event arrives via WebSocket and the chart adds one candle with updated indicators, then stepping to the next trade and verifying order lifecycle events render as trade markers.

**Acceptance Scenarios**:

1. **Given** the user initiates a debug session for a strategy, **When** the session starts, **Then** a WebSocket connection is established, the backend begins in paused state, and the debug screen displays the toolbar, an empty chart, and initial metrics/params panels.
2. **Given** a debug session is active and paused, **When** the user clicks "To Next Bar", **Then** a `next_bar` WebSocket control command is sent, the backend advances to the next `bar` event, and the streamed events are rendered (new candle on chart, updated indicator values, refreshed metrics).
3. **Given** a debug session is active and paused, **When** the user clicks "To Next Trade", **Then** a `next_trade` WebSocket control command is sent, the backend advances to the next order lifecycle event, and all intermediate events (bars, indicators) are rendered along the way plus the trade marker.
4. **Given** a debug session is active, **When** the user enters a date/time and clicks "To Date", **Then** a `run_to` WebSocket control command is sent with the target timestamp, and the backend streams all events up to that point, which are progressively rendered.
5. **Given** a debug session is active, **When** the user clicks "Play", **Then** a `continue` command is sent and incoming events are rendered in real-time as they stream in; clicking "Pause" sends a `pause` command and the simulation halts after the current event.
6. **Given** a debug session is active, **When** the user clicks "Stop", **Then** the WebSocket connection is closed, the debug session is terminated, and the user is navigated back to the dashboard.
7. **Given** events are streaming during a debug session, **When** a `bar` event arrives, **Then** it is appended as a new candle on the chart; when an `ind` event arrives, the corresponding indicator line is extended; when `ord.fill` or `pos` events arrive, trade markers and TP/SL lines are drawn.
8. **Given** there is an open position (a `pos` event with an active position), **When** the chart renders, **Then** the open position is visually indicated with entry marker and TP/SL lines.

---

### User Story 2 - View Detailed Run Report (Priority: P2)

A trader navigates to a run's report screen and sees a full equity curve chart, a metrics summary (Sharpe, Sortino, max drawdown, win rate, etc.), the input parameters used for the run, and—if candle data is available—an interactive candlestick chart with indicator overlays and trade entry/exit markers with TP/SL lines. All charts use TradingView Lightweight Charts. Report data can be sourced from the run's `meta.json` and `events.jsonl` files (parsed to extract equity, candles, indicators, and trades).

**Why this priority**: The report screen delivers the core analytical value—understanding how a strategy performed. It is the natural companion to the debug screen and reuses the same chart components.

**Independent Test**: Can be fully tested by navigating to a run report URL and verifying the equity chart, metrics panel, params panel, and candlestick chart (with overlays) all render correctly with the run's data.

**Acceptance Scenarios**:

1. **Given** the user navigates to a run report, **When** the page loads, **Then** an equity curve chart (TradingView Lightweight Charts line series) is displayed showing portfolio value over time with crosshair tooltips.
2. **Given** the report page is loaded, **When** metrics data is available, **Then** a metrics panel displays all key-value pairs returned by the backend (dynamic keys).
3. **Given** the report page is loaded, **When** params data is available, **Then** an input params panel displays all strategy configuration key-value pairs.
4. **Given** the run has candle data available, **When** the report page loads, **Then** an interactive candlestick chart (TradingView Lightweight Charts candlestick series) is displayed with OHLC candles.
5. **Given** the candlestick chart is rendered, **When** indicator data is available, **Then** each indicator is overlaid as a separate colored line series with a legend.
6. **Given** the candlestick chart is rendered, **When** trade data is available, **Then** entry and exit markers are displayed on the chart, and TP/SL horizontal price lines span each trade's duration.
7. **Given** the run has no candle data, **When** the report loads, **Then** the candlestick chart section is not shown (only equity chart, metrics, and params are displayed).

---

### User Story 3 - Browse and Filter Historical Runs (Priority: P3)

A trader opens the dashboard, selects a strategy and run mode (e.g., Backtest), and sees a table of all matching historical runs with key performance metrics. They sort by Sortino ratio to find top performers and filter by date range to focus on recent results. They click a row to navigate to the detailed report for that run.

**Why this priority**: The runs table is the central hub for navigating between runs. By the time this is built, the backend API for listing runs should be available, reducing the need for mock data.

**Independent Test**: Can be fully tested by loading the dashboard, selecting a strategy, and verifying the table populates with sortable/filterable run data from the backend.

**Acceptance Scenarios**:

1. **Given** the user is on the dashboard, **When** they select a strategy from the sidebar, **Then** the runs table updates to show only runs for that strategy.
2. **Given** a strategy is selected, **When** the user selects the "Backtest" mode tab, **Then** only backtest runs appear in the table.
3. **Given** runs are displayed, **When** the user clicks a column header (e.g., Sortino), **Then** the table sorts by that column in ascending order; clicking again reverses to descending.
4. **Given** runs are displayed, **When** the user sets a date range filter, **Then** only runs within that date range appear.
5. **Given** runs are displayed with dynamic metric columns from the backend, **When** the table renders, **Then** all metric columns are shown including any beyond the fixed set (Ver, RunId, Asset, TF, Sortino, Sharpe, Profit Factor).
6. **Given** a run row is visible, **When** the user clicks the navigate button on that row, **Then** they are taken to the report screen for that run.

---

### User Story 4 - Launch a New Run (Priority: P4)

A trader clicks the "+ Run New" button on the dashboard, edits strategy settings in a JSON editor pre-filled with defaults for the selected strategy and mode, and clicks "Run" to submit. The system shows a loading state, and on success the table refreshes with the new run.

**Why this priority**: Launching runs from the UI is a convenience feature. Traders can launch runs via the CLI or API directly. This is a lower priority than viewing and debugging results.

**Independent Test**: Can be fully tested by clicking "+ Run New", editing the JSON, submitting, and verifying a loading state appears followed by the table refreshing with the new run entry.

**Acceptance Scenarios**:

1. **Given** the user is on the dashboard with a strategy selected, **When** they click "+ Run New", **Then** a slide-over panel opens from the right with a JSON editor and a "Run" button.
2. **Given** the slide-over is open, **When** it renders, **Then** the JSON editor is pre-filled with default settings for the selected strategy and mode.
3. **Given** the user has edited settings in the JSON editor, **When** they click "Run", **Then** a loading state is shown and the run request is submitted to the backend.
4. **Given** the run request succeeds, **When** the response returns, **Then** the slide-over closes and the runs table refreshes to include the new run.
5. **Given** the run request fails, **When** the error response returns, **Then** an error notification is displayed to the user and the slide-over remains open for correction.

---

### User Story 5 - Frontend Development Without Backend (Priority: P2)

A frontend developer enables mock mode and can fully develop, test, and demonstrate the debug and report screens using realistic sample data without a running backend. For the debug screen, mock mode replays a pre-recorded sequence of JSONL events to simulate WebSocket streaming. For the report screen, mock data includes equity curves, candle data, indicators, and trades.

**Why this priority**: Mock mode is critical for parallel frontend/backend development. The debug screen (P1) and report screen (P2) need mock data from the start to enable development before the backend event system and WebSocket server are implemented.

**Independent Test**: Can be fully tested by enabling the mock mode setting, navigating through all screens, and verifying realistic data appears everywhere without any network calls to a real backend or WebSocket server.

**Acceptance Scenarios**:

1. **Given** mock mode is enabled, **When** the user starts and steps through a debug session, **Then** each step returns progressive sample events via a simulated WebSocket (or mock event replay).
2. **Given** mock mode is enabled, **When** the user navigates to a run report, **Then** equity charts, metrics, params, candlestick charts, indicators, and trade markers all render with sample data.
3. **Given** mock mode is enabled, **When** the dashboard loads, **Then** the strategies list and runs table populate with realistic sample data.
4. **Given** mock mode is disabled and the backend is unreachable, **When** any screen loads, **Then** an error state is displayed with a retry option (not mock data).

---

### Edge Cases

- What happens when the backend returns an empty runs list for a strategy/mode combination? The table displays an empty state message (e.g., "No runs found").
- What happens when a run report has no indicator data? The candlestick chart renders without indicator overlays.
- What happens when a run report has no trade data? The candlestick chart renders without trade markers or TP/SL lines.
- What happens when the JSON in the "Run New" editor is invalid? The "Run" button is disabled or a validation error is shown before submission.
- What happens when the WebSocket connection drops during a debug session? An error notification is displayed with a reconnect option or the session is terminated gracefully.
- What happens when a debug step command fails or times out? An error notification is displayed and the user can retry the step or stop the session.
- What happens when the user navigates away from an active debug session? The WebSocket connection is closed and the session is terminated (with a confirmation prompt if the session is in progress).
- What happens when metric keys from the backend contain unexpected formats? Keys are displayed as-is with camelCase-to-Title-Case formatting applied best-effort.
- What happens on screens narrower than the primary desktop target? The sidebar collapses to icons and tables gain horizontal scrolling.
- What happens when events arrive faster than the chart can render during auto-play? Events are buffered and rendered in batches to maintain UI responsiveness.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: System MUST display a sidebar with a strategy selector (dropdown or scrollable list) that fetches and lists all available strategies.
- **FR-002**: System MUST display mode selection buttons (Backtest, Optimization, Walk Forward, Live, Debug) in the sidebar; selecting a mode filters the runs table to that run type.
- **FR-003**: System MUST render a sortable runs table with fixed columns (Ver, RunId, Asset, TF, Sortino, Sharpe, Profit Factor) and dynamically render additional metric columns returned by the backend.
- **FR-004**: System MUST provide date range filters above the runs table for filtering by run start time and backtest period.
- **FR-005**: System MUST provide a navigate action on each table row that takes the user to the detailed report screen for that run.
- **FR-006**: System MUST provide a "+ Run New" button that opens a slide-over panel with a rich JSON editor pre-filled with default settings and a "Run" submit button.
- **FR-007**: System MUST display an equity curve chart using TradingView Lightweight Charts (line series) on the report screen, showing portfolio value over time with crosshair tooltips.
- **FR-008**: System MUST display a metrics panel on the report screen that dynamically renders all key-value metric pairs returned by the backend.
- **FR-009**: System MUST display an input params panel on the report screen that dynamically renders all strategy configuration key-value pairs.
- **FR-010**: System MUST conditionally display an interactive candlestick chart using TradingView Lightweight Charts on the report screen when candle data is available, showing OHLC candles, indicator overlays (as colored line series with legend), trade entry/exit markers, and TP/SL horizontal price lines.
- **FR-011**: System MUST provide a debug toolbar with controls: "To Next Bar" (sends `next_bar` WebSocket command), "To Next Trade" (sends `next_trade`), "To Date" (sends `run_to` with date/time inputs), "Play" (sends `continue`), "Pause" (sends `pause`), and "Stop" (closes session).
- **FR-012**: System MUST establish a WebSocket connection for debug sessions and process incoming JSONL events (`bar`, `ind`, `ord.*`, `pos`, `sig`, `risk`, `run.*`) to progressively build the chart and update metrics.
- **FR-013**: System MUST progressively render candles from `bar` events, extend indicator lines from `ind` events, and place trade markers from order lifecycle events (`ord.fill`, `pos`) on the debug candlestick chart.
- **FR-014**: System MUST update running metrics in the debug screen as events arrive (current equity, P&L, open position info extracted from `pos` events).
- **FR-015**: System MUST support a mock data mode (toggled by configuration) that returns realistic sample data for all screens—including simulated WebSocket event streaming for debug—without requiring a running backend.
- **FR-016**: System MUST display loading skeleton states for charts and tables while data is being fetched.
- **FR-017**: System MUST display error states with retry options when backend requests or WebSocket connections fail.
- **FR-018**: System MUST display toast notifications for user-initiated actions (run started, run failed, debug session ended, etc.).
- **FR-019**: System MUST format camelCase metric and param keys as human-readable Title Case labels.
- **FR-020**: System MUST provide a shared, reusable application shell with a header (app name, navigation links) and footer (version, placeholder links).
- **FR-021**: System MUST use a dark-themed, data-dense visual design optimized for desktop displays (1280px+ primary target) with graceful degradation on narrower screens (collapsible sidebar, horizontally scrollable tables).
- **FR-022**: System MUST use TradingView Lightweight Charts for all chart rendering (equity curves, candlestick charts in both report and debug screens).

### Key Entities

- **Strategy**: A named trading strategy available in the system. Has an identifier and display name. Strategies are the top-level grouping for all runs.
- **Run**: A single execution of a strategy in a given mode (Backtest, Optimization, Walk Forward, Live). Has a unique identifier, associated strategy, mode, version, asset, timeframe, computed performance metrics (dynamic set), and input parameters (dynamic set). A run may or may not have associated candle data.
- **Equity Point**: A single data point on the equity curve, representing portfolio value at a point in time. Belongs to a run.
- **Candle**: An OHLC price bar for a given time interval. Belongs to a run's candle data set.
- **Indicator**: A named computed time series (e.g., SMA, RSI) derived from candle data. Each indicator has a name and a series of time-value data points. Belongs to a run.
- **Trade**: A completed or open trading action with entry time/price, exit time/price, side (long/short), and optional take-profit and stop-loss levels. Belongs to a run.
- **Debug Session**: A stateful, interactive stepping session connected via WebSocket. The backend runs a backtest in paused state and streams JSONL events to the frontend. The frontend sends control commands (`next_bar`, `next_trade`, `run_to`, `continue`, `pause`) to advance execution. The session maintains all backtest state (open positions, indicator buffers, current bar) server-side.
- **Backtest Event**: A JSON object following the canonical event envelope (`ts`, `sq`, `_t`, `src`, `d`) as defined in the event model. Events include market data (`bar`, `bar.mut`), indicators (`ind`, `ind.mut`), signals (`sig`), risk checks (`risk`), order lifecycle (`ord.place`, `ord.fill`, `ord.cancel`, `ord.reject`, `pos`), and system events (`run.start`, `run.end`, `err`, `warn`).

## Assumptions

- The backend REST API will conform to the endpoint contracts described in the frontend requirements document. The debug screen uses a WebSocket interface (not REST endpoints) as defined in the debug feature requirements v2 document.
- The application is a single-user, locally-run tool (no authentication or multi-tenancy required).
- The "Docs" and "Settings" navigation links in the header are placeholders for future development and link to stub pages or are inert.
- The auto-play feature in debug mode sends a `continue` WebSocket command, and the backend streams events continuously until a `pause` command is received. The frontend renders events as they arrive in real-time.
- Mock data fixtures are static JSON files bundled with the frontend; for the debug screen, mock mode replays a pre-recorded sequence of JSONL events to simulate WebSocket streaming.
- The default route (`/`) redirects to `/dashboard`.
- The frontend parses the compact event envelope fields (`ts`, `sq`, `_t`, `src`, `d`) as defined in the event model. Event-type-specific data is in the `d` field.
- Only events from exportable `DataSubscription`s produce `bar` and `ind` events. The frontend does not need to filter subscriptions—it renders all bar/indicator events it receives.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A user can navigate from the dashboard to any run's report screen in 2 clicks or fewer (select strategy, click run row).
- **SC-002**: All three primary screens (Dashboard, Report, Debug) are fully functional and navigable using only mock data, without a running backend.
- **SC-003**: The runs table supports sorting by any column and filtering by date range, with results updating in under 1 second for datasets of up to 500 runs.
- **SC-004**: The report screen renders the equity chart, metrics, params, and candlestick chart (with indicators and trade markers) for a run with 200 candles and 5 trades in under 2 seconds.
- **SC-005**: A user can launch a new run from the dashboard (open panel, edit JSON, submit) in under 30 seconds.
- **SC-006**: The debug screen correctly processes a WebSocket step command and updates the chart with the new bar/indicator/trade data within 1 second.
- **SC-007**: All screens display appropriate loading skeletons during data fetch and error states with retry on failure; no blank or broken screens are shown to the user.
- **SC-008**: The application is fully usable on desktop screens 1280px wide and above; on narrower screens, the sidebar collapses and tables scroll horizontally without layout breakage.
