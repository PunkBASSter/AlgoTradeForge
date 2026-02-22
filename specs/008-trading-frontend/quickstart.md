# Quickstart: Trading Frontend

**Feature**: 008-trading-frontend
**Date**: 2026-02-21

## Prerequisites

- Node.js 20+ (LTS)
- npm or pnpm
- Backend API running at `http://localhost:5000` (or mock mode enabled)

## Project Setup

```bash
# From repo root
cd frontend

# Install dependencies
npm install

# Development server (with mock mode)
NEXT_PUBLIC_MOCK_MODE=true npm run dev

# Development server (with real backend)
npm run dev

# Build for production
npm run build

# Run tests
npm run test

# Lint
npm run lint
```

## Environment Variables

| Variable | Default | Description |
|----------|---------|-------------|
| `NEXT_PUBLIC_API_URL` | `http://localhost:5000` | Backend API base URL |
| `NEXT_PUBLIC_MOCK_MODE` | `false` | Enable mock data mode (no backend required) |

## Project Structure

```
frontend/
├── app/                          # Next.js App Router
│   ├── layout.tsx                # Root layout (shell, header, footer)
│   ├── page.tsx                  # Redirect to /dashboard
│   ├── dashboard/
│   │   ├── layout.tsx            # Dashboard layout (sidebar)
│   │   └── page.tsx              # Dashboard page (runs table)
│   ├── report/
│   │   ├── backtest/[id]/
│   │   │   └── page.tsx          # Backtest report page
│   │   └── optimization/[id]/
│   │       └── page.tsx          # Optimization report page
│   └── debug/
│       └── page.tsx              # Debug screen
├── components/
│   ├── ui/                       # Reusable primitives (Button, Table, Skeleton, Toast, etc.)
│   └── features/
│       ├── charts/               # TradingView chart wrappers (CandlestickChart, EquityChart)
│       ├── dashboard/            # StrategySelector, RunsTable, RunNewPanel, filters
│       ├── report/               # MetricsPanel, ParamsPanel, OptimizationTrialsTable
│       └── debug/                # DebugToolbar, DebugMetrics, SessionConfigEditor
├── hooks/
│   ├── use-backtests.ts          # TanStack Query hooks for backtest API
│   ├── use-optimizations.ts      # TanStack Query hooks for optimization API
│   ├── use-strategies.ts         # TanStack Query hooks for strategies API
│   └── use-debug-websocket.ts    # WebSocket connection + event processing hook
├── lib/
│   ├── services/
│   │   ├── api-client.ts         # Real fetch-based API client
│   │   ├── mock-client.ts        # Mock data provider
│   │   ├── index.ts              # Active client export (based on env var)
│   │   └── mock-data/            # Static JSON fixtures
│   ├── events/
│   │   ├── parser.ts             # JSONL event parser + type discriminator
│   │   └── types.ts              # Event type definitions
│   ├── utils/
│   │   └── format.ts             # camelCase → Title Case, number formatting
│   └── chart-utils.ts            # TradingView chart helper functions
├── types/
│   ├── api.ts                    # API response/request types
│   ├── events.ts                 # Backtest event types (re-exports from lib/events)
│   └── chart.ts                  # Chart-specific data types
├── app/globals.css                # Tailwind @theme design tokens (dark theme, CSS-first config)
├── next.config.ts                # Next.js configuration
├── tsconfig.json                 # TypeScript strict config
├── vitest.config.ts              # Vitest configuration
└── package.json
```

## Key Development Patterns

### Mock Mode

Set `NEXT_PUBLIC_MOCK_MODE=true` to develop without backend. All API calls route through `lib/services/mock-client.ts` which returns static fixtures. Debug WebSocket is simulated with timed event replay.

### Adding a New Screen

1. Create route in `app/{route}/page.tsx`
2. Add loading.tsx and error.tsx for the route
3. Use Server Components for data fetching where possible
4. Wrap chart components in Client Components with `"use client"`

### Chart Components

All chart components are Client Components loaded via `next/dynamic` with `ssr: false`:

```tsx
const CandlestickChart = dynamic(
  () => import("@/components/features/charts/CandlestickChart"),
  { ssr: false, loading: () => <ChartSkeleton /> }
);
```

### API Hooks

Use TanStack Query hooks for all server data:

```tsx
const { data, isLoading, error } = useBacktestList({
  strategyName: selectedStrategy,
  limit: 50,
  offset: 0,
});
```
