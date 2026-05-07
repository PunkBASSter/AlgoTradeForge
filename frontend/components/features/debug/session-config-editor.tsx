"use client";

// T025 - SessionConfigEditor with CodeMirror 6 JSON editor

import { useRef, useEffect, useState, useMemo } from "react";
import { EditorView, keymap } from "@codemirror/view";
import { EditorState } from "@codemirror/state";
import { json, jsonParseLinter } from "@codemirror/lang-json";
import { oneDark } from "@codemirror/theme-one-dark";
import { linter } from "@codemirror/lint";
import { SESSION_KEYS } from "@/lib/constants";
import { basicSetup } from "codemirror";
import { Button } from "@/components/ui/button";
import { useAvailableStrategies } from "@/hooks/use-available-strategies";
import type { StartDebugSessionRequest } from "@/types/api";

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

  // Check for pre-filled config from backtest/optimization "Debug" button
  const prefillRef = useRef<Record<string, unknown> | null>(null);
  useEffect(() => {
    const stored = sessionStorage.getItem(SESSION_KEYS.DEBUG_CONFIG);
    if (!stored) return;
    sessionStorage.removeItem(SESSION_KEYS.DEBUG_CONFIG);
    try {
      const parsed = JSON.parse(stored) as Record<string, unknown>;
      prefillRef.current = parsed;
      if (parsed.strategyName && typeof parsed.strategyName === "string") {
        setSelectedStrategy(parsed.strategyName);
      }
    } catch {
      // ignore invalid JSON
    }
  }, []);

  const descriptor = useMemo(
    () => strategies?.find((s) => s.name === selectedStrategy) ?? null,
    [strategies, selectedStrategy],
  );

  const template = useMemo(
    () => descriptor?.debugSessionTemplate ?? null,
    [descriptor],
  );

  // Merge prefill config on top of the template when both are available
  const initialDoc = useMemo(() => {
    if (prefillRef.current) return prefillRef.current;
    return template;
  }, [template]);

  useEffect(() => {
    if (!editorContainerRef.current) return;

    const state = EditorState.create({
      doc: JSON.stringify(initialDoc, null, 2),
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

    // If a prefill config is pending, use it instead of the template (one-shot)
    const prefill = prefillRef.current;
    prefillRef.current = null;
    const newDoc = JSON.stringify(prefill ?? template, null, 2);
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
      const firstSub = config.dataSubscriptions?.[0];
      if (!firstSub?.assetName || !config.strategyName || !firstSub?.exchange) {
        setValidationError(
          "Missing required fields: dataSubscriptions[0].assetName, strategyName, dataSubscriptions[0].exchange"
        );
        return;
      }
      if (!firstSub.kind) {
        setValidationError(
          'Missing dataSubscriptions[0].kind (must be "TimeBar", "AltBar", "Tick", or "Side")'
        );
        return;
      }
      if (firstSub.kind === "AltBar" && !firstSub.feedId) {
        setValidationError(
          'AltBar subscriptions require a "feedId" (e.g. "EqV_1m_5M")'
        );
        return;
      }
      if (firstSub.kind === "TimeBar" && !firstSub.timeFrame) {
        setValidationError(
          'TimeBar subscriptions require a "timeFrame" (e.g. "1m", "1h")'
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
