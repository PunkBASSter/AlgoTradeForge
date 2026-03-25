"use client";

// T060 - RunNewPanel with slide-over and mode-aware CodeMirror JSON editor

import { useRef, useEffect, useState, useMemo } from "react";
import { useRouter } from "next/navigation";
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
import { ToggleSwitch } from "@/components/ui/toggle-switch";
import type {
  RunBacktestRequest,
  RunOptimizationRequest,
  RunGeneticOptimizationRequest,
  StartLiveSessionRequest,
  StartDebugSessionRequest,
} from "@/types/api";

const EDITOR_EXTENSIONS = [
  basicSetup,
  json(),
  linter(jsonParseLinter()),
  oneDark,
  EditorView.theme({
    "&": { height: "100%" },
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
  const [useGenetic, setUseGenetic] = useState(false);
  const [useDebug, setUseDebug] = useState(false);
  const { toast } = useToast();
  const client = getClient();
  const router = useRouter();

  const { data: strategies } = useAvailableStrategies();

  const descriptor = useMemo(
    () => strategies?.find((s) => s.name === selectedStrategy) ?? null,
    [strategies, selectedStrategy],
  );

  const template = useMemo(() => {
    if (!descriptor) return null;
    if (mode === "backtest")
      return useDebug ? descriptor.debugSessionTemplate : descriptor.backtestTemplate;
    if (mode === "live") return descriptor.liveSessionTemplate;
    if (useGenetic) return descriptor.geneticOptimizationTemplate;
    return descriptor.optimizationTemplate;
  }, [mode, descriptor, useGenetic, useDebug]);

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

  // Reset useGenetic when mode changes away from optimization
  useEffect(() => {
    if (mode !== "optimization") setUseGenetic(false);
  }, [mode]);

  // Reset useDebug when mode leaves backtest
  useEffect(() => {
    if (mode !== "backtest") setUseDebug(false);
  }, [mode]);

  // Update editor content when mode, selectedStrategy, or initialContent changes
  const prevKeyRef = useRef(`${mode}:${selectedStrategy}:${useGenetic}:${useDebug}:${initialContent ? "ic" : ""}`);
  useEffect(() => {
    const key = `${mode}:${selectedStrategy}:${useGenetic}:${useDebug}:${initialContent ? "ic" : ""}`;
    if (!open || !editorViewRef.current || key === prevKeyRef.current) return;
    prevKeyRef.current = key;

    const newDoc = JSON.stringify(initialContent ?? template, null, 2);
    const view = editorViewRef.current;

    view.dispatch({
      changes: { from: 0, to: view.state.doc.length, insert: newDoc },
    });
  }, [open, mode, selectedStrategy, template, initialContent, useGenetic, useDebug]);

  const handleToggle = (genetic: boolean) => {
    if (!descriptor || !editorViewRef.current) {
      setUseGenetic(genetic);
      return;
    }

    const targetTemplate = genetic
      ? descriptor.geneticOptimizationTemplate
      : descriptor.optimizationTemplate;

    const view = editorViewRef.current;

    // Canonical key order — invariant regardless of genetic toggle
    const canonicalOrder = [
      "strategyName",
      "backtestSettings",
      "optimizationSettings",
      ...(genetic ? ["geneticSettings"] as const : []),
      "subscriptionAxis",
      "optimizationAxes",
    ];

    const source: Record<string, unknown> = { ...targetTemplate };

    try {
      const current = JSON.parse(view.state.doc.toString()) as Record<string, unknown>;
      const sharedKeys = [
        "strategyName",
        "backtestSettings",
        "optimizationSettings",
        "subscriptionAxis",
        "optimizationAxes",
      ];
      for (const key of sharedKeys) {
        if (current[key] !== undefined) {
          source[key] = current[key];
        }
      }
    } catch {
      // JSON parse failed — fall back to full template swap
    }

    // Rebuild in canonical order to ensure consistent JSON output
    const merged: Record<string, unknown> = {};
    for (const key of canonicalOrder) {
      if (source[key] !== undefined) merged[key] = source[key];
    }

    const newDoc = JSON.stringify(merged, null, 2);
    view.dispatch({
      changes: { from: 0, to: view.state.doc.length, insert: newDoc },
    });

    setUseGenetic(genetic);
    prevKeyRef.current = `${mode}:${selectedStrategy}:${genetic}:${useDebug}:${initialContent ? "ic" : ""}`;
  };

  const handleDebugToggle = (debug: boolean) => {
    if (!descriptor || !editorViewRef.current) {
      setUseDebug(debug);
      return;
    }

    const targetTemplate = debug
      ? descriptor.debugSessionTemplate
      : descriptor.backtestTemplate;

    const view = editorViewRef.current;
    let merged: Record<string, unknown> = { ...targetTemplate };

    try {
      const current = JSON.parse(view.state.doc.toString()) as Record<string, unknown>;
      const sharedKeys = [
        "strategyName",
        "dataSubscription",
        "backtestSettings",
        "strategyParameters",
      ];
      for (const key of sharedKeys) {
        if (current[key] !== undefined) {
          merged[key] = current[key];
        }
      }
    } catch {
      // JSON parse failed — fall back to full template swap
    }

    const newDoc = JSON.stringify(merged, null, 2);
    view.dispatch({
      changes: { from: 0, to: view.state.doc.length, insert: newDoc },
    });

    setUseDebug(debug);
    prevKeyRef.current = `${mode}:${selectedStrategy}:${useGenetic}:${debug}:${initialContent ? "ic" : ""}`;
  };

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

    if (mode === "backtest" && useDebug) {
      sessionStorage.setItem("debug-session-config", JSON.stringify(parsed as StartDebugSessionRequest));
      sessionStorage.setItem("debug-session-autostart", "true");
      router.push("/debug");
      return;
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
        } else if (useGenetic) {
          const submission = await client.runGeneticOptimization(parsed as RunGeneticOptimizationRequest);
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
      title={`New ${mode === "backtest" ? (useDebug ? "Debug Session" : "Backtest") : mode === "live" ? "Live Session" : "Optimization"}`}
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
        <div className="flex h-full flex-col gap-4">
          <p className="shrink-0 text-sm text-text-secondary">
            Edit the JSON configuration below and click Run.
          </p>
          {mode === "backtest" && (
            <div className="shrink-0">
              <ToggleSwitch
                leftLabel="Backtest"
                rightLabel="Debug"
                checked={useDebug}
                onChange={handleDebugToggle}
                disabled={submitting}
              />
            </div>
          )}
          {mode === "optimization" && (
            <div className="shrink-0">
              <ToggleSwitch
                leftLabel="Grid"
                rightLabel="Genetic"
                checked={useGenetic}
                onChange={handleToggle}
                disabled={submitting}
              />
            </div>
          )}
          <div
            ref={editorContainerRef}
            data-testid="json-editor"
            className="min-h-0 flex-1 rounded-lg overflow-hidden border border-border-default"
          />
          <div className="shrink-0 flex gap-2">
            <Button
              variant="primary"
              onClick={handleSubmit}
              loading={submitting}
              data-testid="submit-run"
            >
              {useDebug ? "Debug" : "Run"}
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
