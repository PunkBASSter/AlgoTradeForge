"use client";

// Phase 3 — sidebar host that switches between Status and new-aggregate cards based on
// the `useDataSelectionStore` mode. Uses `SlideOver` for the panel chrome (focus trap +
// Escape handling come for free).
//
// Phase 6 — for "create" mode, computes the row's safe-trio alt-bar feeds (EqV/EqT/EqD)
// and threads them as `eligibleSources` so the form's Source dropdown can offer
// re-aggregation. The actual eligibility (type-family + threshold-ordering) is checked
// server-side via /aggregation-options when the user picks a source.

import { useMemo } from "react";
import { SlideOver } from "@/components/ui/slide-over";
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

  // SlideOver requires non-null title / children regardless of open state. Provide
  // sensible defaults when no selection exists.
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

  return (
    <SlideOver open={open} onClose={close} title={title}>
      {mode === "view" && exchange && asset && feed && (
        <FeedStatusCard exchange={exchange} asset={asset.asset} feedId={feed.id} />
      )}
      {mode === "create" && exchange && asset && feed && (
        <NewAggregateForm
          exchange={exchange}
          asset={asset.asset}
          sourceFeed={feed}
          eligibleSources={eligibleSources}
          onJobAccepted={onJobAccepted}
        />
      )}
    </SlideOver>
  );
}
