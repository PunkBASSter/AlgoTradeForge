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
  StrategyDescriptor,
  ParameterAxisDescriptor,
  OptimizationAxisOverride,
} from "@/types/api";

const BACKTEST_TEMPLATE: RunBacktestRequest = {
  assetName: "BTCUSDT",
  exchange: "Binance",
  strategyName: "ZigZagBreakout",
  initialCash: 10000,
  startTime: "2025-01-01T00:00:00Z",
  endTime: "2025-12-31T23:59:59Z",
  commissionPerTrade: 0.001,
  slippageTicks: 2,
  timeFrame: "01:00:00",
  strategyParameters: { DzzDepth: 5, MinimumThreshold: 10000, RiskPercentPerTrade: 1 },
};

const OPTIMIZATION_TEMPLATE: RunOptimizationRequest = {
  strategyName: "ZigZagBreakout",
  optimizationAxes: {
    DzzDepth: { min: 1, max: 20, step: 0.5 },
    MinimumThreshold: { min: 5000, max: 50000, step: 5000 },
    RiskPercentPerTrade: { min: 0.5, max: 3, step: 0.5 },
  },
  dataSubscriptions: [
    { asset: "BTCUSDT", exchange: "Binance", timeFrame: "01:00:00" },
  ],
  initialCash: 10000,
  startTime: "2025-01-01T00:00:00Z",
  endTime: "2025-12-31T23:59:59Z",
  commissionPerTrade: 0.001,
  slippageTicks: 2,
  sortBy: "sortinoRatio",
  maxTrialsToKeep: 10000,
  minProfitFactor: 1.2,
  maxDrawdownPct: 40.0,
  minSharpeRatio: 0.5,
  minSortinoRatio: 0.5,
  minAnnualizedReturnPct: 2.0,
};

function buildAxisOverride(axis: ParameterAxisDescriptor): OptimizationAxisOverride {
  if (axis.type === "module" && axis.variants) {
    const variants: Record<string, Record<string, OptimizationAxisOverride> | null> = {};
    for (const v of axis.variants) {
      if (v.axes.length === 0) {
        variants[v.typeKey] = null;
      } else {
        const subAxes: Record<string, OptimizationAxisOverride> = {};
        for (const sub of v.axes) {
          subAxes[sub.name] = buildAxisOverride(sub);
        }
        variants[v.typeKey] = subAxes;
      }
    }
    return { variants };
  }
  return { min: axis.min ?? 0, max: axis.max ?? 1, step: axis.step ?? 1 };
}

function buildBacktestTemplate(descriptor: StrategyDescriptor): RunBacktestRequest {
  return {
    ...BACKTEST_TEMPLATE,
    strategyName: descriptor.name,
    strategyParameters: { ...descriptor.parameterDefaults },
  };
}

function buildOptimizationTemplate(descriptor: StrategyDescriptor): RunOptimizationRequest {
  const axes: Record<string, OptimizationAxisOverride> = {};
  for (const axis of descriptor.optimizationAxes) {
    axes[axis.name] = buildAxisOverride(axis);
  }

  return {
    ...OPTIMIZATION_TEMPLATE,
    strategyName: descriptor.name,
    optimizationAxes: Object.keys(axes).length > 0 ? axes : undefined,
    dataSubscriptions: [
      { asset: "BTCUSDT", exchange: "Binance", timeFrame: "00:15:00" },
    ],
  };
}

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
  selectedStrategy: string | null;
  onSuccess: () => void;
}

export function RunNewPanel({
  open,
  onClose,
  mode,
  selectedStrategy,
  onSuccess,
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
    if (mode === "backtest") {
      return descriptor ? buildBacktestTemplate(descriptor) : BACKTEST_TEMPLATE;
    }
    return descriptor ? buildOptimizationTemplate(descriptor) : OPTIMIZATION_TEMPLATE;
  }, [mode, descriptor]);

  // Create editor once when the slide-over opens
  useEffect(() => {
    if (!open || !editorContainerRef.current) return;

    // Reuse existing editor if it's already attached to this container
    if (editorViewRef.current) return;

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
    // eslint-disable-next-line react-hooks/exhaustive-deps -- mode/strategy changes handled by separate effect below
  }, [open]);

  // Update editor content when mode or selectedStrategy changes
  const prevKeyRef = useRef(`${mode}:${selectedStrategy}`);
  useEffect(() => {
    const key = `${mode}:${selectedStrategy}`;
    if (!open || !editorViewRef.current || key === prevKeyRef.current) return;
    prevKeyRef.current = key;

    const newDoc = JSON.stringify(template, null, 2);
    const view = editorViewRef.current;

    view.dispatch({
      changes: { from: 0, to: view.state.doc.length, insert: newDoc },
    });
  }, [open, mode, selectedStrategy, template]);

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
