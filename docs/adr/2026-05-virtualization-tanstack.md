# ADR — Virtualization library for the alt-bars Data tab

**Date:** 2026-05-02
**Status:** Accepted
**Gates:** P3-10 (alternative-bars-tasks.md)
**Drivers:** P3-12 (asset×feed grid), P3-13 (≥10k cells regression test)

## Context

Phase 3 of the alternative-bars feature adds a Data tab whose central component is an
asset×feed grid: rows are assets (potentially hundreds per exchange), columns are feeds
(union across visible assets — time bars, alt bars, ticks, side feeds). The TRD (§10.1)
calls out **horizontal virtualization** for exchanges with high feed cardinality (e.g.
Binance with hundreds of symbols × dozens of feeds). P3-13 sets a regression bar of
≥10k cells with only on-screen cells materialized in the DOM.

The frontend has no virtualization library today.

## Decision

Use [`@tanstack/react-virtual`](https://tanstack.com/virtual) v3.10+ for **both axes**
(rows + columns) of the grid, sharing a single scroll container with two cooperating
`useVirtualizer` instances (one per axis).

## Alternatives considered

- **`react-window`** — battle-tested, smaller (~6 KB), but its `Grid` component is less
  ergonomic for dynamic columns and would require more glue for the "union of feeds
  across visible assets" use case. Maintained but lower release cadence.
- **`react-virtualized`** — superseded by react-window from the same author; no active
  releases.
- **Custom (CSS / IntersectionObserver)** — rejected because the P3-13 perf target plus
  ergonomic both-axis virtualization is non-trivial to maintain without a tested library.

## Rationale

1. **Same family as TanStack Query** (already in deps via `@tanstack/react-query`) —
   matches the project's existing TanStack toolkit, single mental model.
2. **Hooks-based API** — fits Next.js App Router client-component idioms. `useVirtualizer`
   is composable; the both-axis pattern is documented in TanStack's own docs.
3. **Both-axis support out of the box** — two `useVirtualizer` instances bound to the
   same scroll element is a documented, stable pattern.
4. **Active maintenance** — frequent releases, large issue tracker turnover.
5. **TypeScript-native** — strict typings match the project's `"strict": true` posture.

## Consequences

- New dependency: `@tanstack/react-virtual` ^3.10.0.
- Grid component lives at `frontend/components/features/data/asset-feed-grid.tsx`.
- The Data tab is **strategy-agnostic** (TRD §10.1) and lives outside the
  `app/[strategy]/layout.tsx` chrome — the strategy-scoped tabs (Backtest/Optimization)
  do NOT render on `/data`. This is intentional: data exists once per `(exchange, asset)`
  regardless of strategy.
- Future grids (e.g. an optimization-trial grid) can reuse the same library; no second
  virtualization mental model.
