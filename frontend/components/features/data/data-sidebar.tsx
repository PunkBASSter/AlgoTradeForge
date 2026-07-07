"use client";

// Data tab sidebar. Renders inline as a flex sibling of the main grid (not a
// fixed-position overlay) so the grid auto-resizes to fill remaining width when the
// panel opens and shrinks back when it closes.

import { useEffect, useMemo, useRef } from "react";
import { useDataSelectionStore } from "@/lib/stores/data-selection-store";
import { FeedStatusCard } from "./feed-status-card";
import { NewAggregateForm } from "./new-aggregate-form";
import { ArchiveLoadForm } from "./archive-load-form";
import { parseAltBarFeedId } from "@/lib/data/alt-bar-feed-id";
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

  const title =
    mode === "view"
      ? "Feed status"
      : mode === "create"
        ? "New aggregate bar"
        : mode === "load"
          ? "Load archive data"
          : "";

  // Alt-bar feeds in this row eligible as re-aggregation sources. Same-type-family
  // narrowing happens server-side; the FE just surfaces candidates.
  const eligibleSources = useMemo<FeedCatalogEntry[]>(() => {
    if (!asset || !feed) return [];
    return asset.feeds.filter((f) =>
      f.id !== feed.id
      && f.kind === "OHLCV_AltBar"
      && f.type_code !== null
      && SAFE_REAGG_TYPES.has(f.type_code)
    );
  }, [asset, feed]);

  // Clicking "+" on an existing source: use the feed as-is. Clicking "-" on a missing
  // alt-bar column: parse the column id to derive the real source + pre-filled type/
  // threshold from the column name (e.g. "EqV_1m_1M" → source "1m", type "EqV", threshold "1M").
  const createState = useMemo(() => {
    if (mode !== "create" || !asset || !feed) return null;
    const existing = asset.feeds.find((f) => f.id === feed.id);
    if (existing) return { source: existing, initialTypeCode: "", initialThreshold: "" };
    const parsed = parseAltBarFeedId(feed.id);
    if (!parsed) return { source: feed, initialTypeCode: "", initialThreshold: "" };
    const realSource = asset.feeds.find((f) => f.id === parsed.sourceCode);
    if (!realSource) return null;
    return {
      source: realSource,
      initialTypeCode: parsed.typeCode,
      initialThreshold: parsed.threshold,
    };
  }, [mode, asset, feed]);

  // Escape-to-close + initial focus on open. Tab is intentionally NOT trapped — the
  // panel is part of page flow so Tab moves naturally between grid and panel.
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
      // than the available space.
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
          <FeedStatusCard exchange={exchange} asset={asset} feed={feed} />
        )}
        {mode === "create" && exchange && asset && feed && createState && (
          <NewAggregateForm
            exchange={exchange}
            asset={asset.symbol}
            sourceFeed={createState.source}
            initialTypeCode={createState.initialTypeCode || undefined}
            initialThreshold={createState.initialThreshold || undefined}
            eligibleSources={eligibleSources}
            onJobAccepted={onJobAccepted}
          />
        )}
        {mode === "create" && asset && feed && !createState && (
          <div className="text-accent-red text-sm">
            Cannot create <span className="font-mono">{feed.id}</span>: no source feed in{" "}
            <span className="font-mono">{asset.symbol}</span> matches the required input for this column.
          </div>
        )}
        {mode === "load" && <ArchiveLoadForm />}
      </div>
    </aside>
  );
}
