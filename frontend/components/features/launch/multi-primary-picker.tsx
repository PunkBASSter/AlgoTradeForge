"use client";

// Chip-based multi-select wrapper around FeedPicker for the Optimization launch flow.
// The user adds N Role=Primary candidates that fan out into |primaries| × |combos|
// trials server-side. Side feeds are shared across every primary trial.
//
// Submit shape: a single DSS containing all primary chips + all side chips on
// `subscriptionAxis: [[...primaries, ...sides]]`. The server's ExpandMultiPrimary
// splits this into N single-primary DSSes before enqueuing.

import { useState } from "react";
import { Button } from "@/components/ui/button";
import { FeedPicker, type FeedPickerSelection } from "./feed-picker";
import { formatFeedLabel } from "@/lib/data/feed-label";
import type { FeedCatalogEntry } from "@/types/data-tab";
import type { DataFeedSubscription, DataFeedRole } from "@/types/api";

interface MultiPrimaryPickerProps {
  /** Current set of Role=Primary subscriptions (fan-out candidates). */
  primaries: DataFeedSubscription[];
  /** Current set of Role=Side subscriptions (shared across all trials). */
  sides: DataFeedSubscription[];
  /** Emit on add/remove for primaries. */
  onPrimariesChange: (next: DataFeedSubscription[]) => void;
  /** Emit on add/remove for side feeds. */
  onSidesChange: (next: DataFeedSubscription[]) => void;
  /** Cost-preview text rendered next to the primary header. e.g. "3 × 50 = 150 trials". */
  costPreviewLabel?: string;
  /** Disabled state — used by parents during submit. */
  disabled?: boolean;
}

export function MultiPrimaryPicker({
  primaries,
  sides,
  onPrimariesChange,
  onSidesChange,
  costPreviewLabel,
  disabled,
}: MultiPrimaryPickerProps) {
  return (
    <div className="space-y-4">
      <ChipSection
        title="Primary feeds"
        subtitle="Each primary becomes one optimization run; parameter grid fans out per primary."
        rightLabel={costPreviewLabel}
        role="Primary"
        items={primaries}
        onChange={onPrimariesChange}
        disabled={disabled}
        emptyHint="Pick at least one Primary feed (TimeBar / AltBar / Tick)."
      />
      <ChipSection
        title="Side feeds"
        subtitle="Side feeds attach to every trial (e.g. funding-rate, alt-bar sidecars)."
        role="Side"
        items={sides}
        onChange={onSidesChange}
        disabled={disabled}
        emptyHint="No side feeds attached. Optional — add to surface auxiliary signals on every trial."
      />
    </div>
  );
}

interface ChipSectionProps {
  title: string;
  subtitle: string;
  rightLabel?: string;
  role: DataFeedRole;
  items: DataFeedSubscription[];
  onChange: (next: DataFeedSubscription[]) => void;
  disabled?: boolean;
  emptyHint: string;
}

function ChipSection({
  title,
  subtitle,
  rightLabel,
  role,
  items,
  onChange,
  disabled,
  emptyHint,
}: ChipSectionProps) {
  const [adding, setAdding] = useState(false);
  const [pending, setPending] = useState<FeedPickerSelection | null>(null);

  // Prevent adding the same feed twice in this list. Keyed by exchange|asset|feedId
  // since a single asset's "1h" and another asset's "1h" are distinct candidates.
  const existingKeys = new Set(
    items.map(subToKey),
  );

  const commit = () => {
    if (!pending?.subscription) return;
    if (existingKeys.has(subToKey(pending.subscription))) {
      setPending(null);
      setAdding(false);
      return;
    }
    onChange([...items, pending.subscription]);
    setPending(null);
    setAdding(false);
  };

  const cancel = () => {
    setPending(null);
    setAdding(false);
  };

  const removeAt = (index: number) => {
    onChange(items.filter((_, i) => i !== index));
  };

  return (
    <div className="space-y-2 rounded-lg border border-border-default bg-bg-panel p-3">
      <div className="flex items-baseline justify-between">
        <div>
          <h3 className="text-sm font-semibold text-text-primary">{title}</h3>
          <p className="text-xs text-text-muted">{subtitle}</p>
        </div>
        {rightLabel && (
          <span className="text-xs font-medium text-accent-blue tabular-nums">
            {rightLabel}
          </span>
        )}
      </div>

      {items.length > 0 ? (
        <ul className="flex flex-wrap gap-2" aria-label={`${title} list`}>
          {items.map((sub, i) => (
            <li
              key={`${i}-${subToKey(sub)}`}
              className="flex items-center gap-1.5 rounded-full border border-border-subtle bg-bg-base px-2.5 py-1 text-xs text-text-primary"
            >
              <span className="font-medium">{sub.assetName}</span>
              <span className="text-text-muted">·</span>
              <span>{sub.exchange}</span>
              <span className="text-text-muted">·</span>
              <span className="font-mono">{subToShortLabel(sub)}</span>
              <button
                type="button"
                onClick={() => removeAt(i)}
                disabled={disabled}
                className="ml-1 text-text-muted hover:text-accent-red disabled:opacity-50"
                aria-label={`Remove ${sub.assetName} ${subToShortLabel(sub)}`}
              >
                ×
              </button>
            </li>
          ))}
        </ul>
      ) : (
        <p className="text-xs italic text-text-muted">{emptyHint}</p>
      )}

      {adding ? (
        <div className="space-y-2 rounded-md border border-border-subtle bg-bg-base p-2">
          <FeedPicker
            role={role}
            value={pending}
            onChange={setPending}
            excludeFeedIds={
              pending?.exchange && pending?.asset
                ? new Set(
                    items
                      .filter(
                        (s) => s.assetName === pending.asset && s.exchange === pending.exchange,
                      )
                      .map(subToFeedId),
                  )
                : undefined
            }
            disabled={disabled}
          />
          <div className="flex gap-2">
            <Button
              type="button"
              variant="primary"
              onClick={commit}
              disabled={!pending?.subscription || disabled}
            >
              Add
            </Button>
            <Button type="button" variant="secondary" onClick={cancel} disabled={disabled}>
              Cancel
            </Button>
          </div>
        </div>
      ) : (
        <Button
          type="button"
          variant="secondary"
          onClick={() => setAdding(true)}
          disabled={disabled}
        >
          + Add {role === "Primary" ? "primary" : "side feed"}
        </Button>
      )}
    </div>
  );
}

function subToFeedId(sub: DataFeedSubscription): string {
  switch (sub.kind) {
    case "TimeBar": return sub.timeFrame;
    case "AltBar": return sub.feedId;
    case "Tick": return "ticks";
    case "Side": return sub.feedId;
  }
}

function subToKey(sub: DataFeedSubscription): string {
  return `${sub.exchange}|${sub.assetName}|${subToFeedId(sub)}`;
}

function subToShortLabel(sub: DataFeedSubscription): string {
  switch (sub.kind) {
    case "TimeBar": return sub.timeFrame;
    case "AltBar":
      // Reuse formatFeedLabel by synthesizing a minimal catalog entry; it falls back to
      // feed.id when metadata is absent, so handing it the id is enough.
      return formatFeedLabel({
        id: sub.feedId,
        kind: "OHLCV_AltBar",
        interval: null,
        type_code: null,
        threshold_value: null,
        sidecar: null,
      } as FeedCatalogEntry);
    case "Tick": return "Ticks";
    case "Side": return sub.feedId;
  }
}
