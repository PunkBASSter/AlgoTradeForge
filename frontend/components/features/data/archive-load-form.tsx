"use client";

import { useState } from "react";
import { dataApi, DataApiError } from "@/lib/services/data-api";
import { useLoadJobsStore } from "@/lib/stores/load-jobs-store";
import { Button } from "@/components/ui/button";
import { useToast } from "@/components/ui/toast";
import type { LoadRequestBody } from "@/types/data-tab";

export const ARCHIVE_FEEDS: ReadonlyArray<{
  feedName: string;
  label: string;
  intervals: string[];
  assetTypes: string[];
  allowEmptyInterval?: boolean;
}> = [
  {
    feedName: "candles",
    label: "Candles",
    intervals: ["1m", "5m", "15m", "1h", "4h", "1d"],
    assetTypes: ["spot", "perpetual"],
  },
  {
    feedName: "mark-price",
    label: "Mark price",
    intervals: ["1h"],
    assetTypes: ["perpetual"],
  },
  {
    feedName: "open-interest",
    label: "Open interest",
    intervals: ["5m"],
    assetTypes: ["perpetual"],
  },
  {
    feedName: "ls-ratio-global",
    label: "L/S ratio (global)",
    intervals: ["15m"],
    assetTypes: ["perpetual"],
  },
  {
    feedName: "ls-ratio-top-accounts",
    label: "L/S ratio (top accounts)",
    intervals: ["15m"],
    assetTypes: ["perpetual"],
  },
  {
    feedName: "ls-ratio-top-positions",
    label: "L/S ratio (top positions)",
    intervals: ["1h"],
    assetTypes: ["perpetual"],
  },
  { feedName: "ticks", label: "Ticks (aggTrades)", intervals: [], assetTypes: ["spot", "perpetual"], allowEmptyInterval: true },
  // backend Supports == IsFutures; FE offers only spot|perpetual, so perpetual mirrors it
  { feedName: "funding-rate", label: "Funding rate", intervals: [""], assetTypes: ["perpetual"], allowEmptyInterval: true },
  { feedName: "taker-volume", label: "Taker volume", intervals: ["15m"], assetTypes: ["perpetual"] },
];

function lastDayOfMonth(yearMonth: string): string {
  const [y, m] = yearMonth.split("-").map(Number);
  const day = new Date(Date.UTC(y, m, 0)).getUTCDate();
  return `${yearMonth}-${String(day).padStart(2, "0")}`;
}

const SELECT_CLS =
  "w-full bg-bg-panel border border-border-default rounded px-2 py-1 text-text-primary";
const INPUT_CLS =
  "w-full bg-bg-panel border border-border-default rounded px-2 py-1 font-mono text-text-primary";

export function ArchiveLoadForm() {
  const { toast } = useToast();
  const addJob = useLoadJobsStore((s) => s.addJob);

  const [exchange, setExchange] = useState("binance");
  const [symbol, setSymbol] = useState("");
  const [assetType, setAssetType] = useState<"spot" | "perpetual">("perpetual");
  const [feedName, setFeedName] = useState("");
  const [interval, setInterval] = useState("");
  const [fromMonth, setFromMonth] = useState("");
  const [toMonth, setToMonth] = useState("");

  const [pending, setPending] = useState(false);
  const [errorBanner, setErrorBanner] = useState<string | null>(null);

  const availableFeeds = ARCHIVE_FEEDS.filter((f) => f.assetTypes.includes(assetType));
  const selectedFeed = availableFeeds.find((f) => f.feedName === feedName);
  const availableIntervals = selectedFeed?.intervals ?? [];

  // When assetType changes, reset feedName/interval if no longer valid.
  function handleAssetTypeChange(next: "spot" | "perpetual") {
    setAssetType(next);
    const nextFeeds = ARCHIVE_FEEDS.filter((f) => f.assetTypes.includes(next));
    if (!nextFeeds.some((f) => f.feedName === feedName)) {
      setFeedName("");
      setInterval("");
    } else if (selectedFeed && !selectedFeed.assetTypes.includes(next)) {
      setFeedName("");
      setInterval("");
    }
  }

  // When feed changes, reset interval.
  function handleFeedChange(next: string) {
    setFeedName(next);
    const feed = ARCHIVE_FEEDS.find((f) => f.feedName === next);
    setInterval(feed?.allowEmptyInterval ? "" : (feed?.intervals[0] ?? ""));
  }

  const canSubmit =
    !!exchange.trim() &&
    !!symbol.trim() &&
    !!feedName &&
    (selectedFeed?.allowEmptyInterval || !!interval) &&
    !!fromMonth &&
    !!toMonth &&
    fromMonth <= toMonth &&
    !pending;

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    if (!canSubmit) return;
    setPending(true);
    setErrorBanner(null);

    const from = `${fromMonth}-01`;
    const to = lastDayOfMonth(toMonth);
    const body: LoadRequestBody = {
      exchange: exchange.trim(),
      symbol: symbol.trim(),
      asset_type: assetType,
      feed_name: feedName,
      interval,
      from,
      to,
    };

    try {
      const resp = await dataApi.postLoad(body);
      addJob(resp.job_id, `${symbol.trim()} ${feedName}`);
      toast(`Load started (${resp.job_id.slice(0, 8)})`, "success");
    } catch (err) {
      if (err instanceof DataApiError) {
        if (err.status === 409) {
          const activeJobId = (err.body as { active_job_id: string }).active_job_id;
          addJob(activeJobId, `${symbol.trim()} ${feedName}`);
          toast("Already running — attached", "info");
        } else if (err.status === 422) {
          const msg = (err.body as { message?: string }).message ?? err.message;
          setErrorBanner(msg);
        } else {
          setErrorBanner(err.message);
        }
      } else {
        setErrorBanner(err instanceof Error ? err.message : String(err));
      }
    } finally {
      setPending(false);
    }
  }

  return (
    <form className="space-y-3" onSubmit={handleSubmit}>
      <div className="text-xs text-text-muted uppercase tracking-wide">Archive load</div>

      {errorBanner && (
        <div
          role="alert"
          className="border border-accent-red/50 bg-accent-red/10 text-accent-red px-3 py-2 rounded text-sm"
        >
          {errorBanner}
        </div>
      )}

      <label className="block text-sm">
        <div className="text-text-muted mb-1">Exchange</div>
        <input
          type="text"
          value={exchange}
          onChange={(e) => setExchange(e.target.value)}
          className={INPUT_CLS}
          autoComplete="off"
        />
      </label>

      <label className="block text-sm">
        <div className="text-text-muted mb-1">Symbol</div>
        <input
          type="text"
          value={symbol}
          onChange={(e) => setSymbol(e.target.value.toUpperCase())}
          placeholder="BTCUSDT"
          className={INPUT_CLS}
          autoComplete="off"
        />
      </label>

      <label className="block text-sm">
        <div className="text-text-muted mb-1">Asset type</div>
        <select
          value={assetType}
          onChange={(e) => handleAssetTypeChange(e.target.value as "spot" | "perpetual")}
          className={SELECT_CLS}
        >
          <option value="spot">Spot</option>
          <option value="perpetual">Perpetual</option>
        </select>
      </label>

      <label className="block text-sm">
        <div className="text-text-muted mb-1">Feed</div>
        <select
          value={feedName}
          onChange={(e) => handleFeedChange(e.target.value)}
          className={SELECT_CLS}
        >
          <option value="">— select —</option>
          {availableFeeds.map((f) => (
            <option key={f.feedName} value={f.feedName}>
              {f.label}
            </option>
          ))}
        </select>
      </label>

      {!selectedFeed?.allowEmptyInterval && (
        <label className="block text-sm">
          <div className="text-text-muted mb-1">Interval</div>
          <select
            value={interval}
            onChange={(e) => setInterval(e.target.value)}
            disabled={availableIntervals.length === 0}
            className={SELECT_CLS}
          >
            <option value="">— select —</option>
            {availableIntervals.map((iv) => (
              <option key={iv} value={iv}>
                {iv}
              </option>
            ))}
          </select>
        </label>
      )}

      <label className="block text-sm">
        <div className="text-text-muted mb-1">From (month)</div>
        <input
          type="month"
          value={fromMonth}
          onChange={(e) => setFromMonth(e.target.value)}
          className={INPUT_CLS}
        />
      </label>

      <label className="block text-sm">
        <div className="text-text-muted mb-1">To (month)</div>
        <input
          type="month"
          value={toMonth}
          onChange={(e) => setToMonth(e.target.value)}
          className={INPUT_CLS}
        />
      </label>

      <Button type="submit" variant="primary" disabled={!canSubmit} loading={pending}>
        {pending ? "Submitting…" : "Load"}
      </Button>
    </form>
  );
}
