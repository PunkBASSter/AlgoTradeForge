"use client";

// T025 - SessionConfigEditor with CodeMirror 6 JSON editor

import { useRef, useEffect, useState, useMemo } from "react";
import { EditorView, keymap } from "@codemirror/view";
import { EditorState } from "@codemirror/state";
import { json, jsonParseLinter } from "@codemirror/lang-json";
import { oneDark } from "@codemirror/theme-one-dark";
import { linter } from "@codemirror/lint";
import { basicSetup } from "codemirror";
import { Button } from "@/components/ui/button";
import { useAvailableStrategies } from "@/hooks/use-available-strategies";
import type { StartDebugSessionRequest, StrategyDescriptor } from "@/types/api";

const DEFAULT_CONFIG: StartDebugSessionRequest = {
  assetName: "BTCUSDT",
  exchange: "Binance",
  strategyName: "SmaCrossover",
  initialCash: 10000,
  startTime: "2025-01-01T00:00:00Z",
  endTime: "2025-12-31T23:59:59Z",
  commissionPerTrade: 0.001,
  slippageTicks: 2,
  timeFrame: "00:15:00",
  strategyParameters: {
    fastPeriod: 10,
    slowPeriod: 30,
  },
};

function buildDebugTemplate(descriptor: StrategyDescriptor): StartDebugSessionRequest {
  return {
    ...DEFAULT_CONFIG,
    strategyName: descriptor.name,
    strategyParameters: { ...descriptor.parameterDefaults },
  };
}

interface SessionConfigEditorProps {
  onStart: (config: StartDebugSessionRequest) => void;
  loading?: boolean;
}

export function SessionConfigEditor({
  onStart,
  loading,
}: SessionConfigEditorProps) {
  const editorContainerRef = useRef<HTMLDivElement>(null);
  const editorViewRef = useRef<EditorView | null>(null);
  const [validationError, setValidationError] = useState<string | null>(null);
  const [selectedStrategy, setSelectedStrategy] = useState<string | null>(null);

  const { data: strategies } = useAvailableStrategies();

  const descriptor = useMemo(
    () => strategies?.find((s) => s.name === selectedStrategy) ?? null,
    [strategies, selectedStrategy],
  );

  const template = useMemo(
    () => (descriptor ? buildDebugTemplate(descriptor) : DEFAULT_CONFIG),
    [descriptor],
  );

  useEffect(() => {
    if (!editorContainerRef.current) return;

    const state = EditorState.create({
      doc: JSON.stringify(template, null, 2),
      extensions: [
        basicSetup,
        json(),
        linter(jsonParseLinter()),
        oneDark,
        EditorView.theme({
          "&": { height: "400px" },
          ".cm-scroller": { overflow: "auto" },
        }),
      ],
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
    // eslint-disable-next-line react-hooks/exhaustive-deps -- strategy changes handled by separate effect below
  }, []);

  // Update editor content when selectedStrategy changes
  const prevStrategyRef = useRef(selectedStrategy);
  useEffect(() => {
    if (!editorViewRef.current || selectedStrategy === prevStrategyRef.current) return;
    prevStrategyRef.current = selectedStrategy;

    const newDoc = JSON.stringify(template, null, 2);
    const view = editorViewRef.current;

    view.dispatch({
      changes: { from: 0, to: view.state.doc.length, insert: newDoc },
    });
  }, [selectedStrategy, template]);

  const handleStart = () => {
    if (!editorViewRef.current) return;

    const text = editorViewRef.current.state.doc.toString();
    try {
      const config = JSON.parse(text) as StartDebugSessionRequest;
      if (!config.assetName || !config.strategyName || !config.exchange) {
        setValidationError(
          "Missing required fields: assetName, strategyName, exchange"
        );
        return;
      }
      setValidationError(null);
      onStart(config);
    } catch {
      setValidationError("Invalid JSON");
    }
  };

  return (
    <div className="space-y-4">
      <h2 className="text-lg font-semibold text-text-primary">
        Debug Session Configuration
      </h2>
      <div className="flex items-center gap-2">
        <label htmlFor="debug-strategy-select" className="text-sm text-text-secondary">
          Strategy
        </label>
        <select
          id="debug-strategy-select"
          value={selectedStrategy ?? ""}
          onChange={(e) => setSelectedStrategy(e.target.value || null)}
          className="px-2 py-1.5 text-sm bg-bg-surface border border-border-default rounded text-text-primary"
        >
          <option value="">— Select —</option>
          {strategies?.map((s) => (
            <option key={s.name} value={s.name}>
              {s.name}
            </option>
          ))}
        </select>
      </div>
      <div
        ref={editorContainerRef}
        data-testid="json-editor"
        className="rounded-lg overflow-hidden border border-border-default"
      />
      {validationError && (
        <p className="text-sm text-accent-red">{validationError}</p>
      )}
      <Button variant="primary" onClick={handleStart} loading={loading}>
        Start Debug Session
      </Button>
    </div>
  );
}
