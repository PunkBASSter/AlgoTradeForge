"use client";

// Phase 3 — sidebar host that switches between Status and new-aggregate cards based on
// the `useDataSelectionStore` mode. Uses `SlideOver` for the panel chrome (focus trap +
// Escape handling come for free).

import { SlideOver } from "@/components/ui/slide-over";
import { useDataSelectionStore } from "@/lib/stores/data-selection-store";
import { FeedStatusCard } from "./feed-status-card";
import { NewAggregateForm } from "./new-aggregate-form";

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
          onJobAccepted={onJobAccepted}
        />
      )}
    </SlideOver>
  );
}
