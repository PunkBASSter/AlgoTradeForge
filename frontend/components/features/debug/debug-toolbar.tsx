"use client";

// T026 - Debug toolbar with step/play/stop controls

import { useState } from "react";
import { Button } from "@/components/ui/button";
import { useDebugStore } from "@/lib/stores/debug-store";
import type { DebugCommand } from "@/types/api";

interface DebugToolbarProps {
  onCommand: (command: DebugCommand) => void;
  onStop: () => void;
  disabled?: boolean;
}

export function DebugToolbar({
  onCommand,
  onStop,
  disabled,
}: DebugToolbarProps) {
  const [runToTimestamp, setRunToTimestamp] = useState("");
  const [runToSequence, setRunToSequence] = useState("");
  const [exportEnabled, setExportEnabled] = useState(false);
  const autoStep = useDebugStore((s) => s.autoStep);
  const setAutoStep = useDebugStore((s) => s.setAutoStep);
  const isPlaying = autoStep !== null;

  return (
    <div data-testid="debug-toolbar" className="flex flex-wrap items-center gap-2 p-3 bg-bg-panel rounded-lg border border-border-default">
      {/* Step controls */}
      <Button
        variant="secondary"
        onClick={() => onCommand({ command: "next" })}
        disabled={disabled}
      >
        Next
      </Button>
      <Button
        variant="secondary"
        onClick={() => onCommand({ command: "next_bar" })}
        disabled={disabled}
      >
        To Next Bar
      </Button>
      <Button
        variant="secondary"
        onClick={() => {
          setAutoStep({ kind: "next_trade" });
          onCommand({ command: "next" });
        }}
        disabled={disabled}
      >
        To Next Trade
      </Button>
      <Button
        variant="secondary"
        onClick={() => {
          setAutoStep({ kind: "next_signal" });
          onCommand({ command: "next" });
        }}
        disabled={disabled}
      >
        To Next Signal
      </Button>

      {/* Separator */}
      <div className="w-px h-6 bg-border-default mx-1" />

      {/* Run to timestamp */}
      <div className="flex items-center gap-1">
        <input
          type="text"
          placeholder="Timestamp (ms)"
          aria-label="Run to timestamp (ms)"
          value={runToTimestamp}
          onChange={(e) => setRunToTimestamp(e.target.value)}
          className="w-36 px-2 py-1.5 text-sm bg-bg-surface border border-border-default rounded text-text-primary placeholder:text-text-muted"
          disabled={disabled}
        />
        <Button
          variant="secondary"
          onClick={() => {
            const ts = parseInt(runToTimestamp, 10);
            if (!isNaN(ts)) {
              setAutoStep({ kind: "run_to_timestamp", targetMs: ts });
              onCommand({ command: "next" });
            }
          }}
          disabled={disabled || !runToTimestamp}
        >
          Go
        </Button>
      </div>

      {/* Run to sequence */}
      <div className="flex items-center gap-1">
        <input
          type="text"
          placeholder="Sequence #"
          aria-label="Run to sequence number"
          value={runToSequence}
          onChange={(e) => setRunToSequence(e.target.value)}
          className="w-28 px-2 py-1.5 text-sm bg-bg-surface border border-border-default rounded text-text-primary placeholder:text-text-muted"
          disabled={disabled}
        />
        <Button
          variant="secondary"
          onClick={() => {
            const sq = parseInt(runToSequence, 10);
            if (!isNaN(sq)) {
              setAutoStep({ kind: "run_to_sequence", targetSq: sq });
              onCommand({ command: "next" });
            }
          }}
          disabled={disabled || !runToSequence}
        >
          Go
        </Button>
      </div>

      {/* Separator */}
      <div className="w-px h-6 bg-border-default mx-1" />

      {/* Play/Pause */}
      {isPlaying ? (
        <Button
          variant="secondary"
          onClick={() => setAutoStep(null)}
          disabled={disabled}
        >
          Pause
        </Button>
      ) : (
        <Button
          variant="primary"
          onClick={() => {
            setAutoStep({ kind: "play" });
            onCommand({ command: "next" });
          }}
          disabled={disabled}
        >
          Play
        </Button>
      )}

      {/* Mutation events toggle (bar.mut, ind.mut) */}
      <Button
        variant={exportEnabled ? "primary" : "ghost"}
        onClick={() => {
          const newVal = !exportEnabled;
          setExportEnabled(newVal);
          onCommand({ command: "set_export", mutations: newVal });
        }}
        disabled={disabled}
      >
        {exportEnabled ? "Mutations On" : "Mutations Off"}
      </Button>

      {/* Separator */}
      <div className="w-px h-6 bg-border-default mx-1" />

      {/* Stop */}
      <Button variant="danger" onClick={onStop} disabled={disabled}>
        Stop
      </Button>
    </div>
  );
}
