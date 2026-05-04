"use client";

// Data tab sidebar host. Renders inline as a flex sibling of the main grid (NOT a
// fixed-position overlay) so both stay visible at small viewports — at FullHD or
// laptop widths the SlideOver overlay used to drift off-screen on top of the
// already-wide grid. Inline layout means the grid auto-resizes to fill the remaining
// width when the panel opens, and shrinks back when it closes.
//
// Phase 6 — for "create" mode, computes the row's safe-trio alt-bar feeds (EqV/EqT/EqD)
// and threads them as `eligibleSources` so the form's Source dropdown can offer
// re-aggregation. The actual eligibility (type-family + threshold-ordering) is checked
// server-side via /aggregation-options when the user picks a source.

import { useEffect, useMemo, useRef } from "react";
import { useDataSelectionStore } from "@/lib/stores/data-selection-store";
import { FeedStatusCard } from "./feed-status-card";
import { NewAggregateForm } from "./new-aggregate-form";
import type { FeedCatalogEntry } from "@/types/data-tab";

const SAFE_REAGG_TYPES = new Set(["EqV", "EqT", "EqD"]);

interface Props {
  /** Forwarded to NewAggregateForm so the parent can persist the jobId for SSE resume. */
  onJobAccepted?: (jobId: string, outcomeFeedIdHint: string) => void;
}

export function DataSidebar({ onJobAccepted }: Props) {
  const { mode, exchange, asset, feed, close } = useDataSelectionStore();
  const open = mode !== null;
  const panelRef = useRef<HTMLElement>(null);
  const previousFocusRef = useRef<HTMLElement | null>(null);
  const closeRef = useRef(close);
  closeRef.current = close;

  const title = mode === "view" ? "Feed status" : mode === "create" ? "New aggregate bar" : "";

  // Phase 6 — alt-bar feeds in this row eligible as re-aggregation sources. Same-type-family
  // narrowing happens server-side; the FE just surfaces the candidates.
  const eligibleSources = useMemo<FeedCatalogEntry[]>(() => {
    if (!asset || !feed) return [];
    return asset.feeds.filter((f) =>
      f.id !== feed.id
      && f.kind === "OHLCV_AltBar"
      && f.type_code !== null
      && SAFE_REAGG_TYPES.has(f.type_code)
    );
  }, [asset, feed]);

  // Escape-to-close + initial focus on open. We keep the modal-style affordance even
  // though the panel no longer overlays the page, so keyboard users can dismiss it
  // without reaching for the mouse. We deliberately do NOT trap Tab — the panel is now
  // part of the page flow and Tab should naturally move between grid and panel.
  useEffect(() => {
    if (!open) return;
    previousFocusRef.current = document.activeElement as HTMLElement | null;

    function handleKeyDown(e: KeyboardEvent) {
      if (e.key === "Escape") closeRef.current();
    }
    document.addEventListener("keydown", handleKeyDown);

    requestAnimationFrame(() => {
      const firstFocusable = panelRef.current?.querySelector<HTMLElement>(
        'button, [href], input, select, textarea, [tabindex]:not([tabindex="-1"])'
      );
      firstFocusable?.focus();
    });

    return () => {
      document.removeEventListener("keydown", handleKeyDown);
      previousFocusRef.current?.focus();
    };
  }, [open]);

  if (!open) return null;

  return (
    <aside
      ref={panelRef}
      role="dialog"
      aria-label={title}
      // shrink-0 stops the flex parent from collapsing the panel when the grid is wider
      // than the available space; w-md gives a stable 28rem column.
      className="shrink-0 w-[28rem] border-l border-border-default bg-bg-surface flex flex-col"
    >
      <div className="flex items-center justify-between border-b border-border-default px-4 py-4">
        <h2 className="text-lg font-semibold text-text-primary">{title}</h2>
        <button
          type="button"
          onClick={close}
          className="rounded-md p-1 text-text-muted transition-colors hover:bg-bg-hover hover:text-text-primary"
          aria-label="Close panel"
        >
          <svg
            xmlns="http://www.w3.org/2000/svg"
            className="h-5 w-5"
            viewBox="0 0 20 20"
            fill="currentColor"
            aria-hidden="true"
          >
            <path
              fillRule="evenodd"
              d="M4.293 4.293a1 1 0 011.414 0L10 8.586l4.293-4.293a1 1 0 111.414 1.414L11.414 10l4.293 4.293a1 1 0 01-1.414 1.414L10 11.414l-4.293 4.293a1 1 0 01-1.414-1.414L8.586 10 4.293 5.707a1 1 0 010-1.414z"
              clipRule="evenodd"
            />
          </svg>
        </button>
      </div>

      <div className="flex-1 overflow-y-auto px-4 py-4">
        {mode === "view" && exchange && asset && feed && (
          <FeedStatusCard exchange={exchange} asset={asset.symbol} feedId={feed.id} />
        )}
        {mode === "create" && exchange && asset && feed && (
          <NewAggregateForm
            exchange={exchange}
            asset={asset.symbol}
            sourceFeed={feed}
            eligibleSources={eligibleSources}
            onJobAccepted={onJobAccepted}
          />
        )}
      </div>
    </aside>
  );
}
