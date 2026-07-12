"use client";

// Data tab sidebar. Renders inline as a flex sibling of the main grid (not a
// fixed-position overlay) so the grid auto-resizes to fill remaining width when the
// panel opens and shrinks back when it closes.

import { useEffect, useRef } from "react";
import { useDataSelectionStore } from "@/lib/stores/data-selection-store";
import { FeedStatusCard } from "./feed-status-card";

export function DataSidebar() {
  const { mode, exchange, asset, feed, close } = useDataSelectionStore();
  const open = mode !== null;
  const panelRef = useRef<HTMLElement>(null);
  const previousFocusRef = useRef<HTMLElement | null>(null);
  const closeRef = useRef(close);
  closeRef.current = close;

  const title = mode === "view" ? "Feed status" : "";

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
      </div>
    </aside>
  );
}
