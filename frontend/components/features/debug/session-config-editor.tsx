"use client";

// T025 - SessionConfigEditor with CodeMirror 6 JSON editor

import { useRef, useEffect, useState } from "react";
import { EditorView, keymap } from "@codemirror/view";
import { EditorState } from "@codemirror/state";
import { json, jsonParseLinter } from "@codemirror/lang-json";
import { oneDark } from "@codemirror/theme-one-dark";
import { linter } from "@codemirror/lint";
import { basicSetup } from "codemirror";
import { Button } from "@/components/ui/button";
import type { StartDebugSessionRequest } from "@/types/api";

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

  useEffect(() => {
    if (!editorContainerRef.current) return;

    const state = EditorState.create({
      doc: JSON.stringify(DEFAULT_CONFIG, null, 2),
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
  }, []);

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
