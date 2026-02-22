# Implementation Plan: Trading Frontend

**Branch**: `008-trading-frontend` | **Date**: 2026-02-21 | **Spec**: [spec.md](spec.md)
**Input**: Feature specification from `/specs/008-trading-frontend/spec.md`

## Summary

Build a Next.js 16 trading frontend with three primary screens: Debug (P1), Report (P2), and Dashboard (P3), plus a Run-New panel (P4). The debug screen uses WebSocket for real-time event streaming during interactive strategy stepping. The report screen displays equity charts, metrics, parameters, and candlestick charts with indicators and trade markers. The dashboard provides paged, filterable run tables for backtests and optimizations. All charts use TradingView Lightweight Charts. A mock mode enables full development without a running backend.

**Backend prerequisite**: A new `GET /api/backtests/{id}/events` endpoint returning structured candle/indicator/trade data is required for the report candlestick chart (FR-010).

## Technical Context

**Language/Version**: TypeScript 5.x (strict mode, no `any`) / Node.js 20+
**Framework**: Next.js 16 with App Router (Turbopack default bundler)
**Styling**: Tailwind CSS 4 with CSS-first `@theme` configuration (no `tailwind.config.ts`)
**Charting**: TradingView Lightweight Charts (Client Components with SSR disabled)
**State Management**: TanStack Query (server state) + Zustand (debug session client state)
**JSON Editor**: CodeMirror 6 with `@codemirror/lang-json`
**Testing**: Vitest + React Testing Library (unit/component), Playwright (E2E)
**Target Platform**: Desktop web browser (1280px+ primary, responsive degradation)
**Project Type**: Web frontend (separate `frontend/` directory at repo root)
**Performance Goals**: <1s table filter updates, <2s report render, <1s debug step UI update
**Constraints**: Single-user localhost app, dark theme, no authentication
**Scale**: ~500 runs in table, ~200 candles per report chart, ~5k events per debug session

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle | Status | Notes |
|-----------|--------|-------|
| I. Strategy-as-Code | N/A | Frontend contains no strategy logic |
| II. Test-First | DEFERRED | Vitest + RTL configured (T003) but test tasks not included in current scope. Constitution §II targets strategy verification pipelines, not frontend components. Test tasks to be added in a follow-up iteration. |
| III. Data Integrity | N/A | Frontend is read-only viewer; no data mutation |
| IV. Observability | PASS | Error boundaries, toast notifications, structured console logging |
| V. Separation of Concerns | PASS | "Frontend: Visualization, user interaction, and workflow orchestration only" — exactly this |
| VI. Simplicity & YAGNI | PASS | Minimal screens, no speculative features, sorting deferred |

**Frontend Technology Standards Compliance**:
- [x] TypeScript strict mode, no `any`
- [x] Server Components by default; Client Components for charts and interactive elements
- [x] Loading and error boundaries per route segment
- [x] Tailwind utility classes; design tokens in config
- [x] Chart components as Client Components with cleanup on unmount
- [x] TanStack Query for server state; Zustand for debug session state
- [x] Constitution code organization structure (`app/`, `components/{ui,features}`, `lib/`, `hooks/`, `types/`)

**Post-Phase-1 re-check**: All gates still pass. No new complexity introduced beyond constitution-prescribed patterns.

**Constitution Deviation Notes**:

- **React Server Actions**: Constitution mandates "MUST use React Server Actions for mutations where appropriate." This app uses client-side TanStack Query mutations instead, which is more appropriate here because: (1) the debug screen requires real-time WebSocket state management tightly coupled with mutation triggers, (2) Server Actions add server-side rendering overhead with no benefit for a single-user localhost app, (3) TanStack Query provides automatic cache invalidation and optimistic updates that align with the SPA-like interaction model.
- **Data windowing**: Constitution requires "efficient data windowing for large datasets." At current scale (~200 candles per report, ~5k events per debug session), TradingView Lightweight Charts handles the dataset natively without windowing. If scale exceeds ~50k data points in future, implement viewport-based windowing with `IChartApi.timeScale().getVisibleLogicalRange()`.

## Project Structure

### Documentation (this feature)

```text
specs/008-trading-frontend/
├── plan.md              # This file
├── spec.md              # Feature specification
├── research.md          # Phase 0 research decisions
├── data-model.md        # Phase 1 TypeScript type definitions
├── quickstart.md        # Phase 1 development quickstart
├── contracts/
│   └── api-endpoints.md # Phase 1 API endpoint contracts
├── checklists/
│   └── requirements.md  # Spec quality checklist
└── tasks.md             # Phase 2 output (via /speckit.tasks)
```

### Source Code (repository root)

```text
frontend/
├── app/                          # Next.js App Router pages and layouts
│   ├── layout.tsx                # Root layout (shell: header, footer, providers)
│   ├── page.tsx                  # Redirect / → /dashboard
│   ├── dashboard/
│   │   ├── layout.tsx            # Dashboard layout (sidebar with strategy selector)
│   │   ├── page.tsx              # Dashboard page (runs table + filters)
│   │   ├── loading.tsx           # Skeleton loader
│   │   └── error.tsx             # Error boundary
│   ├── report/
│   │   ├── backtest/[id]/
│   │   │   ├── page.tsx          # Backtest report (equity, metrics, params, candles)
│   │   │   ├── loading.tsx
│   │   │   └── error.tsx
│   │   └── optimization/[id]/
│   │       ├── page.tsx          # Optimization report (summary + trials table)
│   │       ├── loading.tsx
│   │       └── error.tsx
│   └── debug/
│       ├── page.tsx              # Debug screen (config editor → live stepping)
│       ├── loading.tsx
│       └── error.tsx
├── components/
│   ├── ui/                       # Reusable primitives
│   │   ├── button.tsx
│   │   ├── table.tsx
│   │   ├── skeleton.tsx
│   │   ├── toast.tsx
│   │   ├── tabs.tsx
│   │   ├── slide-over.tsx
│   │   └── pagination.tsx
│   └── features/
│       ├── charts/
│       │   ├── candlestick-chart.tsx   # TradingView candlestick + overlays
│       │   ├── equity-chart.tsx        # TradingView line chart for equity
│       │   └── chart-skeleton.tsx
│       ├── dashboard/
│       │   ├── strategy-selector.tsx
│       │   ├── runs-table.tsx
│       │   ├── run-new-panel.tsx
│       │   └── run-filters.tsx
│       ├── report/
│       │   ├── metrics-panel.tsx
│       │   ├── params-panel.tsx
│       │   └── optimization-trials-table.tsx
│       └── debug/
│           ├── debug-toolbar.tsx
│           ├── debug-metrics.tsx
│           └── session-config-editor.tsx
├── hooks/
│   ├── use-backtests.ts
│   ├── use-optimizations.ts
│   ├── use-strategies.ts
│   └── use-debug-websocket.ts
├── lib/
│   ├── services/
│   │   ├── api-client.ts
│   │   ├── mock-client.ts
│   │   ├── index.ts
│   │   └── mock-data/
│   │       ├── strategies.json
│   │       ├── backtests.json
│   │       ├── optimizations.json
│   │       ├── equity.json
│   │       ├── events.json
│   │       └── debug-events.jsonl
│   ├── events/
│   │   ├── parser.ts
│   │   └── types.ts
│   ├── stores/
│   │   └── debug-store.ts         # Zustand store for debug session state
│   └── utils/
│       ├── format.ts              # camelCase → Title Case, numbers
│       └── chart-utils.ts         # TradingView helpers
├── types/
│   ├── api.ts
│   ├── events.ts
│   └── chart.ts
├── next.config.ts
├── tsconfig.json
├── vitest.config.ts
└── package.json
```

**Structure Decision**: Standalone `frontend/` directory at repo root, following the constitution's prescribed code organization. Separate from the existing .NET `src/` and `tests/` directories. This is a new project, not a modification of the existing .NET solution.

## Complexity Tracking

No constitution violations. No complexity justifications needed.
