"use client";

// T060 - RunNewPanel with slide-over and mode-aware CodeMirror JSON editor

import { useRef, useEffect, useState, useMemo, useCallback } from "react";
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
import { FeedPicker, type FeedPickerSelection } from "@/components/features/launch/feed-picker";
import { MultiPrimaryPicker } from "@/components/features/launch/multi-primary-picker";
import { useThresholdProfiles } from "@/hooks/use-threshold-profiles";
import type {
  DataFeedSubscription,
  RunBacktestRequest,
  RunOptimizationRequest,
  RunGeneticOptimizationRequest,
  EvaluateOptimizationRequest,
  OptimizationEvaluation,
  StartLiveSessionRequest,
  StartDebugSessionRequest,
} from "@/types/api";
import { SESSION_KEYS } from "@/lib/constants";

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

/** Extract evaluation-relevant fields from editor JSON for cache key. */
function computeEvalCacheKey(text: string, genetic: boolean): string | null {
  try {
    const obj = JSON.parse(text) as Record<string, unknown>;
    const keyParts = {
      strategyName: obj.strategyName,
      optimizationAxes: obj.optimizationAxes,
      subscriptionAxis: obj.subscriptionAxis,
      dataSubscriptions: obj.dataSubscriptions,
      maxCombinations: (obj.optimizationSettings as Record<string, unknown> | undefined)?.maxCombinations,
      geneticSettings: genetic ? obj.geneticSettings : undefined,
      mode: genetic ? "Genetic" : "BruteForce",
    };
    return JSON.stringify(keyParts);
  } catch {
    return null;
  }
}

function formatNumber(n: number): string {
  return n.toLocaleString();
}

/** Synthesize a stable picker-feed-id from a subscription (used to seed dropdown state). */
function subFeedIdForSelection(sub: DataFeedSubscription): string {
  switch (sub.kind) {
    case "TimeBar": return sub.timeFrame;
    case "AltBar": return sub.feedId;
    case "Tick": return "ticks";
    case "Side": return sub.feedId;
  }
}

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
  const [evaluation, setEvaluation] = useState<OptimizationEvaluation | null>(null);
  const [evaluating, setEvaluating] = useState(false);
  const evaluationCacheRef = useRef<Map<string, OptimizationEvaluation>>(new Map());
  // Phase 4 (P4-17/18/19): polymorphic primary + side selections. One DSS containing
  // [...primaries, ...sides] becomes the canonical subscriptionAxis on the wire — the
  // server-side ExpandMultiPrimary fans the multi-primary case into N child runs (TRD §9.6).
  // Backtest/Live restrict primaries to length === 1 via the picker UI.
  const [primaries, setPrimaries] = useState<DataFeedSubscription[]>([]);
  const [sides, setSides] = useState<DataFeedSubscription[]>([]);
  const [runValidation, setRunValidation] = useState(false);
  const [thresholdProfile, setThresholdProfile] = useState("Crypto-Standard");
  const [maxThreads, setMaxThreads] = useState(0);
  const suppressEditorSyncRef = useRef(false);
  const useGeneticRef = useRef(useGenetic);
  useGeneticRef.current = useGenetic;
  const { toast } = useToast();
  const client = getClient();
  const router = useRouter();

  const { data: strategies } = useAvailableStrategies();
  const { data: profiles } = useThresholdProfiles();

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

  const isOptimization = mode === "optimization";

  // Handle editor doc changes: check cache, clear or restore evaluation, sync pickers
  const handleDocChange = useCallback((text: string) => {
    // Sync picker state from editor (unless the editor change was triggered BY a picker).
    // Phase 4: subscriptionAxis on the wire is DataFeedSubscription[][]. Each inner DSS
    // splits by `role` into primaries + sides for picker display; the server-side
    // ExpandMultiPrimary handles multi-primary fan-out at submit time.
    if (!suppressEditorSyncRef.current) {
      try {
        const obj = JSON.parse(text) as Record<string, unknown>;
        const axis = obj.subscriptionAxis as DataFeedSubscription[][] | undefined;
        if (axis && Array.isArray(axis) && axis.length > 0) {
          // Flatten across DSSes — every primary in any DSS becomes a fan-out candidate.
          // Multi-DSS request shape was historical; the new flow puts all primaries +
          // sides in a single DSS, but reading legacy multi-DSS JSON should still work.
          const flat = axis.flat();
          const nextPrimaries = flat.filter((s) => s.role === "Primary");
          const nextSides = flat.filter((s) => s.role === "Side");
          setPrimaries(nextPrimaries);
          setSides(nextSides);
        } else if (!axis) {
          setPrimaries(prev => prev.length === 0 ? prev : []);
          setSides(prev => prev.length === 0 ? prev : []);
        }
      } catch {
        // Invalid JSON — don't update picker state
      }
    }

    if (!isOptimization) return;
    const cacheKey = computeEvalCacheKey(text, useGeneticRef.current);
    if (cacheKey) {
      const cached = evaluationCacheRef.current.get(cacheKey);
      if (cached) {
        setEvaluation(cached);
        return;
      }
    }
    setEvaluation(null);
  }, [isOptimization]);

  // Push picker state into the editor's subscriptionAxis, kept as a single DSS containing
  // [...primaries, ...sides]. Server-side ExpandMultiPrimary fans the multi-primary case.
  const syncEditorFromPickers = useCallback(
    (nextPrimaries: DataFeedSubscription[], nextSides: DataFeedSubscription[]) => {
      const view = editorViewRef.current;
      if (!view) return;
      try {
        const obj = JSON.parse(view.state.doc.toString()) as Record<string, unknown>;
        const combined = [...nextPrimaries, ...nextSides];
        if (combined.length > 0) {
          obj.subscriptionAxis = [combined];
        } else {
          delete obj.subscriptionAxis;
        }
        const newDoc = JSON.stringify(obj, null, 2);
        suppressEditorSyncRef.current = true;
        view.dispatch({
          changes: { from: 0, to: view.state.doc.length, insert: newDoc },
        });
        suppressEditorSyncRef.current = false;
      } catch {
        // Editor JSON is invalid — can't sync
      }
    },
    [],
  );

  const handlePrimariesChange = useCallback(
    (next: DataFeedSubscription[]) => {
      setPrimaries(next);
      syncEditorFromPickers(next, sides);
    },
    [sides, syncEditorFromPickers],
  );

  const handleSidesChange = useCallback(
    (next: DataFeedSubscription[]) => {
      setSides(next);
      syncEditorFromPickers(primaries, next);
    },
    [primaries, syncEditorFromPickers],
  );

  // Single-primary (Backtest/Live): wraps the multi-list shape behind FeedPicker's
  // selection state. We synthesize FeedPickerSelection from the current primary so the
  // dropdowns reflect the picked value when the panel reopens.
  const singlePrimarySelection: FeedPickerSelection | null = useMemo(() => {
    if (primaries.length === 0) return null;
    const sub = primaries[0];
    return {
      exchange: sub.exchange,
      asset: sub.assetName,
      feedId: subFeedIdForSelection(sub),
      subscription: sub,
    };
  }, [primaries]);

  const handleSinglePrimaryChange = useCallback(
    (sel: FeedPickerSelection | null) => {
      const next = sel?.subscription ? [sel.subscription] : [];
      handlePrimariesChange(next);
    },
    [handlePrimariesChange],
  );

  // Create editor once when the slide-over opens
  useEffect(() => {
    if (!open || !editorContainerRef.current) return;

    // Reuse existing editor if it's already attached to this container
    if (editorViewRef.current) return;

    const initialDoc = initialContent ?? template;
    const extensions = [
      ...EDITOR_EXTENSIONS,
      EditorView.updateListener.of((update) => {
        if (update.docChanged) {
          handleDocChange(update.state.doc.toString());
        }
      }),
    ];
    const state = EditorState.create({
      doc: JSON.stringify(initialDoc, null, 2),
      extensions,
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

  // Auto-detect genetic mode from initialContent (e.g. optimization re-run)
  useEffect(() => {
    if (mode !== "optimization" || !initialContent) return;
    setUseGenetic("geneticSettings" in initialContent);
  }, [mode, initialContent]);

  // Reset useDebug when mode leaves backtest
  useEffect(() => {
    if (mode !== "backtest") setUseDebug(false);
  }, [mode]);

  // Clear evaluation when mode changes away from optimization
  useEffect(() => {
    if (!isOptimization) {
      setEvaluation(null);
    }
  }, [isOptimization]);

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
    setEvaluation(null);
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
    const merged: Record<string, unknown> = { ...targetTemplate };

    try {
      const current = JSON.parse(view.state.doc.toString()) as Record<string, unknown>;
      const sharedKeys = [
        "strategyName",
        "dataSubscriptions",
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

  const handleEvaluate = async () => {
    if (!editorViewRef.current) return;

    const text = editorViewRef.current.state.doc.toString();
    let parsed: Record<string, unknown>;
    try {
      parsed = JSON.parse(text) as Record<string, unknown>;
    } catch {
      toast("Invalid JSON", "error");
      return;
    }

    if (!parsed.strategyName) {
      toast("Missing required field: strategyName", "error");
      return;
    }

    // Check cache first
    const cacheKey = computeEvalCacheKey(text, useGenetic);
    if (cacheKey) {
      const cached = evaluationCacheRef.current.get(cacheKey);
      if (cached) {
        setEvaluation(cached);
        return;
      }
    }

    setEvaluating(true);
    try {
      const req: EvaluateOptimizationRequest = {
        strategyName: parsed.strategyName as string,
        optimizationAxes: parsed.optimizationAxes as EvaluateOptimizationRequest["optimizationAxes"],
        subscriptionAxis: parsed.subscriptionAxis as EvaluateOptimizationRequest["subscriptionAxis"],
        optimizationSettings: parsed.optimizationSettings as EvaluateOptimizationRequest["optimizationSettings"],
        mode: useGenetic ? "Genetic" : "BruteForce",
        geneticSettings: useGenetic ? parsed.geneticSettings as EvaluateOptimizationRequest["geneticSettings"] : undefined,
      };

      const result = await client.evaluateOptimization(req);
      setEvaluation(result);

      // Cache the result
      if (cacheKey) {
        evaluationCacheRef.current.set(cacheKey, result);
      }
    } catch (err) {
      toast(String(err), "error");
    } finally {
      setEvaluating(false);
    }
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
      const dsArr = obj.dataSubscriptions as Record<string, unknown>[] | undefined;
      const ds = dsArr?.[0];
      const bs = obj.backtestSettings as Record<string, unknown> | undefined;
      const missing: string[] = [];
      if (!ds?.assetName) missing.push("dataSubscriptions[0].assetName");
      if (!ds?.exchange) missing.push("dataSubscriptions[0].exchange");
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
      sessionStorage.setItem(SESSION_KEYS.DEBUG_CONFIG, JSON.stringify(parsed as StartDebugSessionRequest));
      sessionStorage.setItem(SESSION_KEYS.DEBUG_AUTOSTART, "true");
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
          // Power-user escape hatch: the FeedPicker only emits a single DSS for backtest
          // mode, so this multi-DSS branch fires only when the user hand-edits the JSON
          // editor to add multiple DSSes. Each becomes its own backtest submission.
          const btReq = parsed as RunBacktestRequest & { subscriptionAxis?: DataFeedSubscription[][] };
          if (btReq.subscriptionAxis && btReq.subscriptionAxis.length > 1) {
            const results: string[] = [];
            for (const dss of btReq.subscriptionAxis) {
              const perDssReq: RunBacktestRequest = {
                ...btReq,
                dataSubscriptions: dss,
              };
              const submission = await client.runBacktest(perDssReq);
              results.push(submission.id);
            }
            toast(`${results.length} backtests submitted`, "success");
            setActiveRunId(results[0]);
            return;
          }
          const submission = await client.runBacktest(btReq as RunBacktestRequest);
          runId = submission.id;
        } else if (useGenetic) {
          const genReq = parsed as RunGeneticOptimizationRequest;
          if (runValidation) {
            genReq.validate = true;
            genReq.thresholdProfileName = thresholdProfile;
          }
          if (maxThreads > 0) genReq.maxThreads = maxThreads;
          const submission = await client.runGeneticOptimization(genReq);
          runId = submission.id;
        } else {
          const optReq = parsed as RunOptimizationRequest;
          if (runValidation) {
            optReq.validate = true;
            optReq.thresholdProfileName = thresholdProfile;
          }
          if (maxThreads > 0) optReq.maxThreads = maxThreads;
          const submission = await client.runOptimization(optReq);
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
    setEvaluation(null);
    evaluationCacheRef.current.clear();
    onClose();
  };

  const handleRunComplete = () => {
    onSuccess();
  };

  // Determine if Run should be enabled for optimization mode
  const canRun = !isOptimization || (evaluation !== null && !evaluation.exceedsMaxCombinations);

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
            {isOptimization
              ? "Edit the JSON configuration below, click Evaluate to preview, then Run."
              : "Edit the JSON configuration below and click Run."}
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
          {isOptimization && (
            <div className="shrink-0">
              <ToggleSwitch
                leftLabel="Grid"
                rightLabel="Genetic"
                checked={useGenetic}
                onChange={handleToggle}
                disabled={submitting || evaluating}
              />
            </div>
          )}
          {isOptimization && (
            <div className="shrink-0 space-y-2">
              <div className="flex items-center gap-4">
                <label className="flex items-center gap-2 text-sm text-text-secondary">
                  <input
                    type="checkbox"
                    checked={runValidation}
                    onChange={(e) => setRunValidation(e.target.checked)}
                    disabled={submitting}
                    className="rounded border-border-default"
                  />
                  Run Validation
                </label>
                <label className="flex items-center gap-2 text-sm text-text-secondary">
                  <span>Max Threads</span>
                  <input
                    type="number"
                    min={0}
                    value={maxThreads}
                    onChange={(e) => setMaxThreads(Math.max(0, parseInt(e.target.value) || 0))}
                    disabled={submitting}
                    className="w-16 rounded border border-border-default bg-bg-surface px-2 py-1 text-sm text-text-primary"
                    title="0 = use all CPU cores"
                  />
                </label>
              </div>
              {runValidation && profiles && (
                <label className="flex items-center gap-2 text-sm text-text-secondary">
                  <span>Threshold Profile</span>
                  <select
                    value={thresholdProfile}
                    onChange={(e) => setThresholdProfile(e.target.value)}
                    disabled={submitting}
                    className="rounded border border-border-default bg-bg-surface px-2 py-1 text-sm text-text-primary"
                  >
                    {profiles.map((p) => (
                      <option key={p.name} value={p.name}>
                        {p.name}
                      </option>
                    ))}
                  </select>
                </label>
              )}
            </div>
          )}
          {mode !== "live" && (
            <div className="shrink-0">
              {isOptimization ? (
                <MultiPrimaryPicker
                  primaries={primaries}
                  sides={sides}
                  onPrimariesChange={handlePrimariesChange}
                  onSidesChange={handleSidesChange}
                  costPreviewLabel={
                    evaluation && primaries.length > 0
                      ? `${primaries.length} × ${formatNumber(evaluation.totalCombinations)} = ${formatNumber(primaries.length * evaluation.totalCombinations)} trials`
                      : undefined
                  }
                  disabled={submitting}
                />
              ) : (
                <div className="rounded-lg border border-border-default bg-bg-panel p-3 space-y-2">
                  <h3 className="text-sm font-semibold text-text-primary">Primary feed</h3>
                  <FeedPicker
                    role="Primary"
                    value={singlePrimarySelection}
                    onChange={handleSinglePrimaryChange}
                    disabled={submitting}
                  />
                </div>
              )}
            </div>
          )}
          <div
            ref={editorContainerRef}
            data-testid="json-editor"
            className="min-h-0 flex-1 rounded-lg overflow-hidden border border-border-default"
          />
          {isOptimization && evaluation && (
            <div
              data-testid="evaluation-result"
              className={`shrink-0 rounded-lg px-3 py-2 text-sm ${
                evaluation.exceedsMaxCombinations
                  ? "bg-red-900/30 border border-red-700 text-red-300"
                  : "bg-green-900/30 border border-green-700 text-green-300"
              }`}
            >
              {evaluation.geneticConfig ? (
                <span>
                  Search space: {formatNumber(evaluation.totalCombinations)}
                  {" | "}Dims: {evaluation.effectiveDimensions}
                  {" | "}Pop: {evaluation.geneticConfig.populationSize}
                  {" | "}Gens: {evaluation.geneticConfig.maxGenerations}
                  {" | "}Evals: {formatNumber(evaluation.geneticConfig.maxEvaluations)}
                </span>
              ) : evaluation.exceedsMaxCombinations ? (
                <span>
                  {formatNumber(evaluation.totalCombinations)} combinations
                  {" \u2014 "}exceeds limit of {formatNumber(evaluation.maxCombinations)}
                </span>
              ) : (
                <span>{formatNumber(evaluation.totalCombinations)} combinations</span>
              )}
            </div>
          )}
          <div className="shrink-0 flex gap-2">
            {isOptimization && (
              <Button
                variant="secondary"
                onClick={handleEvaluate}
                loading={evaluating}
                disabled={submitting}
                data-testid="evaluate-optimization"
              >
                Evaluate
              </Button>
            )}
            <Button
              variant="primary"
              onClick={handleSubmit}
              loading={submitting}
              disabled={!canRun || evaluating}
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
