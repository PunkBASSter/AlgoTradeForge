"use client";

import { useEffect, useMemo, useRef, useState } from "react";
import { EditorState } from "@codemirror/state";
import { EditorView } from "@codemirror/view";
import { json } from "@codemirror/lang-json";
import { oneDark } from "@codemirror/theme-one-dark";
import { ChevronIcon } from "@/components/ui/chevron-icon";
import { formatCurrency } from "@/lib/utils/format";
import type {
  StartDebugSessionRequest,
  DataFeedSubscription,
  BacktestSettingsInput,
} from "@/types/api";

interface Props {
  config: StartDebugSessionRequest;
}

export function SessionSettingsCard({ config }: Props) {
  const [expanded, setExpanded] = useState(true);

  const formattedParams = useMemo(() => {
    if (!config.strategyParameters) return "";
    const keys = Object.keys(config.strategyParameters);
    if (keys.length === 0) return "";
    return JSON.stringify(config.strategyParameters, null, 2);
  }, [config.strategyParameters]);

  return (
    <div className="rounded-lg border border-border-default bg-bg-panel">
      <button
        type="button"
        onClick={() => setExpanded((v) => !v)}
        className="w-full flex items-center justify-between px-4 py-3 hover:bg-bg-hover transition-colors rounded-lg"
        aria-expanded={expanded}
      >
        <div className="flex items-center gap-3">
          <ChevronIcon expanded={expanded} />
          <span className="text-sm font-semibold uppercase tracking-wider text-text-muted">
            Session Settings
          </span>
          <span className="text-sm font-medium text-text-primary">
            {config.strategyName}
          </span>
        </div>
        <span className="text-xs text-text-muted">
          {config.dataSubscriptions.length}{" "}
          {config.dataSubscriptions.length === 1 ? "feed" : "feeds"}
        </span>
      </button>

      {expanded && (
        <div className="border-t border-border-subtle px-4 py-4 space-y-4">
          <Section label="Strategy">
            <div className="font-mono text-sm text-text-primary">
              {config.strategyName}
            </div>
          </Section>

          <Section label="Backtest Settings">
            <BacktestSettingsRows settings={config.backtestSettings} />
          </Section>

          <Section label="Data Feeds">
            <DataFeedsList subscriptions={config.dataSubscriptions} />
          </Section>

          {formattedParams && (
            <Section label="Strategy Parameters">
              <ParamsJsonViewer json={formattedParams} />
            </Section>
          )}
        </div>
      )}
    </div>
  );
}

function Section({
  label,
  children,
}: {
  label: string;
  children: React.ReactNode;
}) {
  return (
    <div className="space-y-2">
      <div className="text-xs text-text-muted uppercase tracking-wide">
        {label}
      </div>
      {children}
    </div>
  );
}

function BacktestSettingsRows({ settings }: { settings: BacktestSettingsInput }) {
  const rows: { label: string; value: string }[] = [
    { label: "Initial Cash", value: formatCurrency(settings.initialCash) },
    { label: "Start", value: formatIsoDateTime(settings.startTime) },
    { label: "End", value: formatIsoDateTime(settings.endTime) },
  ];
  if (settings.commissionPerTrade != null) {
    rows.push({
      label: "Commission / Trade",
      value: formatCurrency(settings.commissionPerTrade),
    });
  }
  if (settings.slippageTicks != null) {
    rows.push({
      label: "Slippage (ticks)",
      value: String(settings.slippageTicks),
    });
  }

  return (
    <div className="space-y-1">
      {rows.map((r) => (
        <div key={r.label} className="flex justify-between gap-2">
          <span className="text-sm text-text-secondary">{r.label}</span>
          <span className="text-sm font-medium text-text-primary font-mono">
            {r.value}
          </span>
        </div>
      ))}
    </div>
  );
}

function DataFeedsList({
  subscriptions,
}: {
  subscriptions: DataFeedSubscription[];
}) {
  if (subscriptions.length === 0) {
    return (
      <div className="text-sm text-text-secondary">No data feeds configured.</div>
    );
  }
  return (
    <div className="border border-border-subtle rounded divide-y divide-border-subtle">
      {subscriptions.map((sub, i) => (
        <div
          key={`${sub.exchange}/${sub.assetName}/${subKey(sub)}/${i}`}
          className="flex items-center justify-between gap-2 px-3 py-2"
        >
          <div className="flex items-center gap-2 min-w-0">
            <KindBadge sub={sub} />
            <span className="text-xs text-text-muted">{sub.exchange}</span>
            <span className="text-text-muted">/</span>
            <span className="font-mono text-sm text-text-primary truncate">
              {sub.assetName}
            </span>
          </div>
          <SubscriptionDetail sub={sub} />
        </div>
      ))}
    </div>
  );
}

function KindBadge({ sub }: { sub: DataFeedSubscription }) {
  const isPrimary = sub.role === "Primary";
  return (
    <span
      className={`px-1.5 py-0.5 rounded text-[10px] font-mono uppercase tracking-wide ${
        isPrimary
          ? "bg-accent-blue/20 text-accent-blue"
          : "bg-bg-surface text-text-muted border border-border-subtle"
      }`}
      title={`${sub.kind} · ${sub.role}`}
    >
      {sub.kind}
    </span>
  );
}

function SubscriptionDetail({ sub }: { sub: DataFeedSubscription }) {
  switch (sub.kind) {
    case "TimeBar":
      return (
        <span className="font-mono text-xs text-text-muted whitespace-nowrap">
          {sub.timeFrame}
        </span>
      );
    case "AltBar":
    case "Side":
      return (
        <span className="font-mono text-xs text-text-secondary whitespace-nowrap">
          {sub.feedId}
        </span>
      );
    case "Tick":
      return (
        <span className="font-mono text-xs text-text-muted whitespace-nowrap">
          ticks
        </span>
      );
  }
}

function subKey(sub: DataFeedSubscription): string {
  switch (sub.kind) {
    case "TimeBar":
      return sub.timeFrame;
    case "AltBar":
    case "Side":
      return sub.feedId;
    case "Tick":
      return "tick";
  }
}

function ParamsJsonViewer({ json: source }: { json: string }) {
  const containerRef = useRef<HTMLDivElement>(null);
  const viewRef = useRef<EditorView | null>(null);

  useEffect(() => {
    if (!containerRef.current) return;

    if (viewRef.current) {
      viewRef.current.dispatch({
        changes: {
          from: 0,
          to: viewRef.current.state.doc.length,
          insert: source,
        },
      });
      return;
    }

    const state = EditorState.create({
      doc: source,
      extensions: [
        json(),
        oneDark,
        EditorState.readOnly.of(true),
        EditorView.editable.of(false),
        EditorView.theme({
          "&": { fontSize: "12px", maxHeight: "320px" },
          ".cm-scroller": { overflow: "auto" },
        }),
      ],
    });
    viewRef.current = new EditorView({ state, parent: containerRef.current });

    return () => {
      viewRef.current?.destroy();
      viewRef.current = null;
    };
  }, [source]);

  return <div ref={containerRef} className="border border-border-subtle rounded" />;
}

function formatIsoDateTime(iso: string): string {
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return iso;
  const pad = (n: number) => String(n).padStart(2, "0");
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${pad(d.getHours())}:${pad(d.getMinutes())}`;
}
