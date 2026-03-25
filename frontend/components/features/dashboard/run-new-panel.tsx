"use client";

// T060 - RunNewPanel with slide-over and mode-aware CodeMirror JSON editor

import { useRef, useEffect, useState, useMemo } from "react";
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
import { useAvailableStrategies } from "@/hooks/use-available-strategies";
import type {
  RunBacktestRequest,
  RunOptimizationRequest,
  StartLiveSessionRequest,
} from "@/types/api";

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
  mode: "backtest" | "optimization" | "live";
  selectedStrategy: string | null;
  onSuccess: () => void;
  initialContent?: Record<string, unknown> | null;
}

export function RunNewPanel({
  open,
  onClose,
  mode,
  selectedStrategy,
  onSuccess,
  initialContent,
}: RunNewPanelProps) {
  const editorContainerRef = useRef<HTMLDivElement>(null);
  const editorViewRef = useRef<EditorView | null>(null);
  const [submitting, setSubmitting] = useState(false);
  const [activeRunId, setActiveRunId] = useState<string | null>(null);
  const { toast } = useToast();
  const client = getClient();

  const { data: strategies } = useAvailableStrategies();

  const descriptor = useMemo(
    () => strategies?.find((s) => s.name === selectedStrategy) ?? null,
    [strategies, selectedStrategy],
  );

  const template = useMemo(() => {
    if (!descriptor) return null;
    if (mode === "backtest") return descriptor.backtestTemplate;
    if (mode === "live") return descriptor.liveSessionTemplate;
    return descriptor.optimizationTemplate;
  }, [mode, descriptor]);

  // Create editor once when the slide-over opens
  useEffect(() => {
    if (!open || !editorContainerRef.current) return;

    // Reuse existing editor if it's already attached to this container
    if (editorViewRef.current) return;

    const initialDoc = initialContent ?? template;
    const state = EditorState.create({
      doc: JSON.stringify(initialDoc, null, 2),
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
    // eslint-disable-next-line react-hooks/exhaustive-deps -- mode/strategy changes handled by separate effect below
  }, [open]);

  // Update editor content when mode, selectedStrategy, or initialContent changes
  const prevKeyRef = useRef(`${mode}:${selectedStrategy}:${initialContent ? "ic" : ""}`);
  useEffect(() => {
    const key = `${mode}:${selectedStrategy}:${initialContent ? "ic" : ""}`;
    if (!open || !editorViewRef.current || key === prevKeyRef.current) return;
    prevKeyRef.current = key;

    const newDoc = JSON.stringify(initialContent ?? template, null, 2);
    const view = editorViewRef.current;

    view.dispatch({
      changes: { from: 0, to: view.state.doc.length, insert: newDoc },
    });
  }, [open, mode, selectedStrategy, template, initialContent]);

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
      const ds = obj.dataSubscription as Record<string, unknown> | undefined;
      const bs = obj.backtestSettings as Record<string, unknown> | undefined;
      const missing: string[] = [];
      if (!ds?.assetName) missing.push("dataSubscription.assetName");
      if (!ds?.exchange) missing.push("dataSubscription.exchange");
      if (!bs?.initialCash) missing.push("backtestSettings.initialCash");
      if (!bs?.startTime) missing.push("backtestSettings.startTime");
      if (!bs?.endTime) missing.push("backtestSettings.endTime");
      if (!obj.strategyName) missing.push("strategyName");
      if (missing.length > 0) {
        toast(`Missing required fields: ${missing.join(", ")}`, "error");
        return;
      }
    } else if (mode === "live") {
      const missing = ["strategyName", "initialCash"]
        .filter((k) => obj[k] === undefined || obj[k] === null);
      if (missing.length > 0) {
        toast(`Missing required fields: ${missing.join(", ")}`, "error");
        return;
      }
    } else {
      const bs = obj.backtestSettings as Record<string, unknown> | undefined;
      const missing: string[] = [];
      if (!obj.strategyName) missing.push("strategyName");
      if (!bs?.initialCash) missing.push("backtestSettings.initialCash");
      if (!bs?.startTime) missing.push("backtestSettings.startTime");
      if (!bs?.endTime) missing.push("backtestSettings.endTime");
      if (missing.length > 0) {
        toast(`Missing required fields: ${missing.join(", ")}`, "error");
        return;
      }
    }

    setSubmitting(true);
    try {
      if (mode === "live") {
        await client.startLiveSession(parsed as StartLiveSessionRequest);
        toast("Live session started", "success");
        onSuccess();
        onClose();
      } else {
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
      }
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
      title={`New ${mode === "backtest" ? "Backtest" : mode === "live" ? "Live Session" : "Optimization"}`}
    >
      {activeRunId ? (
        <div className="space-y-4">
          <RunProgress
            runId={activeRunId}
            mode={mode as "backtest" | "optimization"}
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
            data-testid="json-editor"
            className="rounded-lg overflow-hidden border border-border-default"
          />
          <div className="flex gap-2">
            <Button
              variant="primary"
              onClick={handleSubmit}
              loading={submitting}
              data-testid="submit-run"
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
