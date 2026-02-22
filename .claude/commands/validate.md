---
description: Launch frontend and backend, validate with Playwright, then stop both
---

## User Input

```text
$ARGUMENTS
```

## Instructions

You are validating that the AlgoTradeForge backend (WebApi) and frontend (Next.js dashboard) launch correctly and can communicate. Use Playwright MCP tools for browser interaction.

### 1. Start the Backend

Launch the .NET WebApi in the background:

```bash
cd /c/repos/trading/csharp/AlgoTradeForge && dotnet run --project src/AlgoTradeForge.WebApi
```

Run this in the background. Wait ~8 seconds, then verify it's listening by checking the output for `Now listening on:` lines.

**Expected ports** (from `launchSettings.json`):
- HTTPS: `https://localhost:55908`
- HTTP: `http://localhost:55909`

### 2. Start the Frontend

Launch the Next.js dev server in the background with the correct API URL:

```bash
cd /c/repos/trading/csharp/AlgoTradeForge/frontend && NEXT_PUBLIC_API_URL=http://localhost:55909 npm run dev
```

**Critical:** The frontend defaults to `http://localhost:5000` which is WRONG. You must set `NEXT_PUBLIC_API_URL=http://localhost:55909` to match the backend's HTTP port.

Wait ~10 seconds, then verify it's ready by checking for `Ready in` in the output.

**Expected port**: `http://localhost:3000`

### 3. Validate with Playwright

Use the Playwright MCP tools (`browser_navigate`, `browser_snapshot`, `browser_click`, `browser_wait_for`, `browser_console_messages`, `browser_network_requests`, `browser_take_screenshot`) to validate:

**Note on Playwright limitations:** Playwright MCP can capture console messages and network requests via dedicated tools (`browser_console_messages`, `browser_network_requests`), but it does NOT provide access to full browser DevTools (Performance tab, Memory profiler, Application storage, etc.). For deep DevTools inspection, manual browser testing is needed.

#### 3a. Dashboard Page

1. Navigate to `http://localhost:3000`
2. Wait 3 seconds for API calls to complete
3. Verify redirect to `/dashboard`
4. Check snapshot includes:
   - Header with "AlgoTradeForge" branding
   - Navigation links: "Dashboard", "Debug"
   - Strategies sidebar
   - Dashboard heading with "+ Run New" button
   - Backtest/Optimization tab list
   - Filter fields (Asset, Exchange, Timeframe, From, To)
   - Runs table with column headers (Version, Run ID, Asset, Exchange, TF, Sortino, Sharpe, PF, Max DD, Win Rate, Net Profit)
   - Pagination controls
   - Footer "AlgoTradeForge v0.1.0"
5. Check console messages for errors (level: "error") — expect 0
6. Check network requests — expect:
   - `GET /api/strategies` → 200
   - `GET /api/backtests?limit=50&offset=0&standaloneOnly=true` → 200

#### 3b. Optimization Tab

1. Click the "Optimization" tab
2. Verify table headers change to: Version, Run ID, Asset, Exchange, TF, Combinations, Sort By, Duration
3. Check network requests for: `GET /api/optimizations?limit=50&offset=0` → 200

#### 3c. Run New Panel

1. Click "+ Run New" button
2. Verify slide-out dialog opens with:
   - "New Backtest" heading
   - CodeMirror JSON editor with pre-filled config
   - Run and Cancel buttons
3. Close the panel

#### 3d. Debug Page

1. Click "Debug" navigation link
2. Verify navigation to `/debug`
3. Check snapshot includes:
   - "Debug Session" heading
   - "Debug Session Configuration" sub-heading
   - CodeMirror JSON editor with default config (SmaCrossover strategy)
   - "Start Debug Session" button

#### 3e. Swagger UI (Backend)

1. Navigate to `http://localhost:55909/swagger`
2. Wait 3 seconds for Swagger to load
3. Verify:
   - Title: "AlgoTradeForge API v1"
   - Endpoint groups: Backtests (4), Debug (4), Optimizations (3), Strategies (1)
   - Schemas section with DTOs

#### 3f. (Optional) Take Screenshots

Take screenshots for documentation:
- `dashboard-validation.png` — Frontend dashboard
- `swagger-validation.png` — Swagger UI (full page)

### 4. Report Results

Summarize the validation results:

```
Validation Summary
==================
Backend:    [PASS/FAIL] — Ports, Swagger UI
Frontend:   [PASS/FAIL] — Dashboard, Debug page, Navigation
API Calls:  [PASS/FAIL] — All endpoints returned 200
Console:    [PASS/FAIL] — Error count: N
Network:    [PASS/FAIL] — All requests listed with status codes
```

If any failures, describe what went wrong and suggest fixes.

### 5. Cleanup

**Always stop both applications when done:**

1. Close the Playwright browser
2. Stop the frontend background task
3. Stop the backend background task

Verify both tasks are stopped by checking their status.

### Troubleshooting

| Issue | Fix |
|---|---|
| Frontend shows "Loading..." forever | Check `NEXT_PUBLIC_API_URL` is set to `http://localhost:55909` |
| CORS errors in console | Verify backend CORS allows `http://localhost:3000` (configured in Program.cs) |
| Backend won't start | Check if ports 55908/55909 are already in use: `tasklist \| findstr dotnet` |
| Frontend won't start | Check if port 3000 is in use; try `npm install` first if `node_modules` missing |
| SSL certificate warnings | Use HTTP port (55909) instead of HTTPS (55908) for API calls |
