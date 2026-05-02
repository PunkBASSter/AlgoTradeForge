"use client";

// Phase 3 — read-only Monaco/CodeMirror viewer for a feed's `feeds.json` entry (P3-15).
// The TRD §10.1 references "Monaco" but CodeMirror 6 (already in deps) is interchangeable
// for a read-only JSON viewer — that decision is recorded in the virtualization ADR.
//
// Banner: when the feed's manifest entry has imbalance_reconstruction_method ==
// "m1_taker_buy_proxy", show the EqI yellow-banner using the canonical server-supplied
// copy from /aggregation-options (TRD §10.1, P3-19). Pulled via parent.

import { useEffect, useMemo, useRef } from "react";
import { useQuery } from "@tanstack/react-query";
import { EditorState } from "@codemirror/state";
import { EditorView } from "@codemirror/view";
import { json } from "@codemirror/lang-json";
import { oneDark } from "@codemirror/theme-one-dark";
import { dataApi } from "@/lib/services/data-api";
import { pickEqiBanner } from "@/lib/data/eqi-banner";

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

  // Eligibility-options is the canonical source of `warnings[]` for the EqI banner.
  // Note: Status card needs warnings even though the form is the primary consumer —
  // a feed with `imbalance_reconstruction_method = m1_taker_buy_proxy` displays the
  // banner regardless of where the user came from.
  const eligibility = useQuery({
    queryKey: ["data", "aggregation-options", exchange, asset, feedId],
    queryFn: ({ signal }) => dataApi.getAggregationOptions(exchange, asset, feedId, signal),
    // Aggregation-options is meaningful for time-bar source feeds (eligible types listed)
    // and harmless for alt bars (returns empty arrays). Always fetch.
  });

  const formattedJson = useMemo(() => {
    if (!status.data) return "";
    return JSON.stringify(status.data.definition, null, 2);
  }, [status.data]);

  const isProxyFeed =
    status.data?.definition.fidelity?.imbalance_reconstruction_method ===
    "m1_taker_buy_proxy";
  const banner = isProxyFeed && eligibility.data
    ? pickEqiBanner(eligibility.data.warnings)
    : null;

  // Initialize / update the CodeMirror view whenever the JSON changes.
  useEffect(() => {
    if (!editorContainerRef.current) return;

    if (editorViewRef.current) {
      // Update content of an existing view by replacing the entire doc.
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
        // Read-only mode: the user can scroll/select but not type. `editable: () => false`
        // is necessary IN ADDITION to readOnly to suppress the IME ghost cursor that
        // otherwise renders.
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
