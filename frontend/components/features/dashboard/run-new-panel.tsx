"use client";

// T060 - RunNewPanel with slide-over and mode-aware CodeMirror JSON editor

import { useRef, useEffect, useState } from "react";
import { EditorView } from "@codemirror/view";
import { EditorState } from "@codemirror/state";
import { json, jsonParseLinter } from "@codemirror/lang-json";
import { oneDark } from "@codemirror/theme-one-dark";
import { linter } from "@codemirror/lint";
import { basicSetup } from "codemirror";
import { SlideOver } from "@/components/ui/slide-over";
import { Button } from "@/components/ui/button";
import { useToast } from "@/components/ui/toast";
import { getClient } from "@/lib/services";
import { RunProgress } from "@/components/features/dashboard/run-progress";
import type { RunBacktestRequest, RunOptimizationRequest } from "@/types/api";

const BACKTEST_TEMPLATE: RunBacktestRequest = {
  assetName: "BTCUSDT",
  exchange: "Binance",
  strategyName: "SmaCrossover",
  initialCash: 10000,
  startTime: "2025-01-01T00:00:00Z",
  endTime: "2025-12-31T23:59:59Z",
  commissionPerTrade: 0.001,
  slippageTicks: 2,
  timeFrame: "00:15:00",
  strategyParameters: { fastPeriod: 10, slowPeriod: 30 },
};

const OPTIMIZATION_TEMPLATE: RunOptimizationRequest = {
  strategyName: "SmaCrossover",
  optimizationAxes: {
    fastPeriod: { min: 5, max: 20, step: 5 },
    slowPeriod: { min: 20, max: 50, step: 10 },
  },
  dataSubscriptions: [
    { asset: "BTCUSDT", exchange: "Binance", timeFrame: "00:15:00" },
  ],
  initialCash: 10000,
  startTime: "2025-01-01T00:00:00Z",
  endTime: "2025-12-31T23:59:59Z",
  commissionPerTrade: 0.001,
  slippageTicks: 2,
  sortBy: "sortinoRatio",
};

const EDITOR_EXTENSIONS = [
  basicSetup,
  json(),
  linter(jsonParseLinter()),
  oneDark,
  EditorView.theme({
    "&": { height: "400px" },
    ".cm-scroller": { overflow: "auto" },
  }),
];

interface RunNewPanelProps {
  open: boolean;
  onClose: () => void;
  mode: "backtest" | "optimization";
  onSuccess: () => void;
}

export function RunNewPanel({
  open,
  onClose,
  mode,
  onSuccess,
}: RunNewPanelProps) {
  const editorContainerRef = useRef<HTMLDivElement>(null);
  const editorViewRef = useRef<EditorView | null>(null);
  const [submitting, setSubmitting] = useState(false);
  const [activeRunId, setActiveRunId] = useState<string | null>(null);
  const { toast } = useToast();
  const client = getClient();

  // Create editor once when the slide-over opens
  useEffect(() => {
    if (!open || !editorContainerRef.current) return;

    // Reuse existing editor if it's already attached to this container
    if (editorViewRef.current) return;

    const template =
      mode === "backtest" ? BACKTEST_TEMPLATE : OPTIMIZATION_TEMPLATE;

    const state = EditorState.create({
      doc: JSON.stringify(template, null, 2),
      extensions: EDITOR_EXTENSIONS,
    });

    const view = new EditorView({
      state,
      parent: editorContainerRef.current,
    });

    editorViewRef.current = view;

    return () => {
      view.destroy();
      editorViewRef.current = null;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps -- mode changes handled by separate effect below
  }, [open]);

  // Update editor content when mode changes (without recreating the view)
  const prevModeRef = useRef(mode);
  useEffect(() => {
    if (!open || !editorViewRef.current || mode === prevModeRef.current) return;
    prevModeRef.current = mode;

    const template =
      mode === "backtest" ? BACKTEST_TEMPLATE : OPTIMIZATION_TEMPLATE;
    const newDoc = JSON.stringify(template, null, 2);
    const view = editorViewRef.current;

    view.dispatch({
      changes: { from: 0, to: view.state.doc.length, insert: newDoc },
    });
  }, [open, mode]);

  const handleSubmit = async () => {
    if (!editorViewRef.current) return;

    const text = editorViewRef.current.state.doc.toString();
    let parsed: unknown;
    try {
      parsed = JSON.parse(text);
    } catch {
      toast("Invalid JSON", "error");
      return;
    }

    // Basic runtime validation of required fields
    const obj = parsed as Record<string, unknown>;
    if (mode === "backtest") {
      const missing = ["assetName", "exchange", "strategyName", "initialCash", "startTime", "endTime"]
        .filter((k) => obj[k] === undefined || obj[k] === null);
      if (missing.length > 0) {
        toast(`Missing required fields: ${missing.join(", ")}`, "error");
        return;
      }
    } else {
      const missing = ["strategyName", "initialCash", "startTime", "endTime"]
        .filter((k) => obj[k] === undefined || obj[k] === null);
      if (missing.length > 0) {
        toast(`Missing required fields: ${missing.join(", ")}`, "error");
        return;
      }
    }

    setSubmitting(true);
    try {
      let runId: string;
      if (mode === "backtest") {
        const submission = await client.runBacktest(parsed as RunBacktestRequest);
        runId = submission.id;
      } else {
        const submission = await client.runOptimization(parsed as RunOptimizationRequest);
        runId = submission.id;
      }
      toast(`${mode === "backtest" ? "Backtest" : "Optimization"} submitted`, "success");
      setActiveRunId(runId);
    } catch (err) {
      toast(String(err), "error");
    } finally {
      setSubmitting(false);
    }
  };

  const handleClose = () => {
    if (activeRunId) {
      setActiveRunId(null);
      onSuccess();
    }
    onClose();
  };

  const handleRunComplete = () => {
    onSuccess();
  };

  return (
    <SlideOver
      open={open}
      onClose={handleClose}
      title={`New ${mode === "backtest" ? "Backtest" : "Optimization"}`}
    >
      {activeRunId ? (
        <div className="space-y-4">
          <RunProgress
            runId={activeRunId}
            mode={mode}
            onComplete={handleRunComplete}
          />
          <Button variant="ghost" onClick={handleClose}>
            Close
          </Button>
        </div>
      ) : (
        <div className="space-y-4">
          <p className="text-sm text-text-secondary">
            Edit the JSON configuration below and click Run.
          </p>
          <div
            ref={editorContainerRef}
            className="rounded-lg overflow-hidden border border-border-default"
          />
          <div className="flex gap-2">
            <Button
              variant="primary"
              onClick={handleSubmit}
              loading={submitting}
            >
              Run
            </Button>
            <Button variant="ghost" onClick={handleClose}>
              Cancel
            </Button>
          </div>
        </div>
      )}
    </SlideOver>
  );
}
