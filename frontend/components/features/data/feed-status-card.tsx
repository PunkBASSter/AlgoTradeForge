"use client";

// Read-only CodeMirror viewer for a feed's `feeds.json` entry. When the manifest's
// imbalance_reconstruction_method is one of the *_proxy values (EqI / EqID / EqIT on
// time-bar), shows the matching server-supplied banner from /aggregation-options.

import { useEffect, useMemo, useRef } from "react";
import { useQuery } from "@tanstack/react-query";
import { EditorState } from "@codemirror/state";
import { EditorView } from "@codemirror/view";
import { json } from "@codemirror/lang-json";
import { oneDark } from "@codemirror/theme-one-dark";
import { dataApi } from "@/lib/services/data-api";
import { pickProxyBanner } from "@/lib/data/eqi-banner";

interface Props {
  exchange: string;
  asset: string;
  feedId: string;
}

export function FeedStatusCard({ exchange, asset, feedId }: Props) {
  const editorContainerRef = useRef<HTMLDivElement>(null);
  const editorViewRef = useRef<EditorView | null>(null);

  const status = useQuery({
    queryKey: ["data", "feed-status", exchange, asset, feedId],
    queryFn: ({ signal }) => dataApi.getFeedStatus(exchange, asset, feedId, signal),
  });

  // Canonical source of `warnings[]` for the EqI banner. Harmless for alt bars (returns
  // empty arrays); always fetch.
  const eligibility = useQuery({
    queryKey: ["data", "aggregation-options", exchange, asset, feedId],
    queryFn: ({ signal }) => dataApi.getAggregationOptions(exchange, asset, feedId, signal),
  });

  const formattedJson = useMemo(() => {
    if (!status.data) return "";
    return JSON.stringify(status.data.definition, null, 2);
  }, [status.data]);

  // Pick the banner copy that matches this feed's reconstruction method. Returns null for
  // tick-source methods (no warning needed) and for non-imbalance feeds.
  const reconstructionMethod =
    status.data?.definition.fidelity?.imbalance_reconstruction_method ?? null;
  const banner = eligibility.data
    ? pickProxyBanner(eligibility.data.warnings, reconstructionMethod)
    : null;

  useEffect(() => {
    if (!editorContainerRef.current) return;

    if (editorViewRef.current) {
      editorViewRef.current.dispatch({
        changes: {
          from: 0,
          to: editorViewRef.current.state.doc.length,
          insert: formattedJson,
        },
      });
      return;
    }

    const state = EditorState.create({
      doc: formattedJson,
      extensions: [
        json(),
        oneDark,
        // `editable: () => false` is required in addition to readOnly to suppress the
        // IME ghost cursor that otherwise renders.
        EditorState.readOnly.of(true),
        EditorView.editable.of(false),
        EditorView.theme({
          "&": { fontSize: "12px", maxHeight: "320px" },
          ".cm-scroller": { overflow: "auto" },
        }),
      ],
    });
    editorViewRef.current = new EditorView({
      state,
      parent: editorContainerRef.current,
    });

    return () => {
      editorViewRef.current?.destroy();
      editorViewRef.current = null;
    };
  }, [formattedJson]);

  return (
    <div className="space-y-3">
      <div className="text-xs text-text-muted uppercase tracking-wide">Status</div>
      <div className="font-mono text-sm text-text-primary">{feedId}</div>

      {banner && (
        <div
          role="alert"
          className="border border-accent-yellow/50 bg-accent-yellow/10 text-accent-yellow px-3 py-2 rounded text-sm"
        >
          {banner}
        </div>
      )}

      {status.isLoading && (
        <div className="text-text-secondary text-sm">Loading status…</div>
      )}
      {status.error && (
        <div className="text-accent-red text-sm">
          {status.error instanceof Error ? status.error.message : String(status.error)}
        </div>
      )}
      {status.data && (
        <div ref={editorContainerRef} className="border border-border-subtle rounded" />
      )}
    </div>
  );
}
