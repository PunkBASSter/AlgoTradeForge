"use client";

import { useRef, useEffect, useMemo } from "react";
import type { TrialEquityData } from "@/types/validation";

const MONTH_LABELS = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];
const CELL_SIZE = 48;
const LABEL_WIDTH = 40;
const HEADER_HEIGHT = 24;

interface MonthlyReturn {
  year: number;
  month: number; // 0-11
  returnPct: number;
}

function computeMonthlyReturns(trial: TrialEquityData): MonthlyReturn[] {
  if (trial.timestamps.length === 0) return [];

  const monthMap = new Map<string, { startEquity: number; endEquity: number; year: number; month: number }>();

  for (let i = 0; i < trial.timestamps.length; i++) {
    const d = new Date(trial.timestamps[i]);
    const year = d.getUTCFullYear();
    const month = d.getUTCMonth();
    const key = `${year}-${month}`;

    const entry = monthMap.get(key);
    if (!entry) {
      monthMap.set(key, { startEquity: trial.equity[i], endEquity: trial.equity[i], year, month });
    } else {
      entry.endEquity = trial.equity[i];
    }
  }

  return [...monthMap.values()].map((e) => ({
    year: e.year,
    month: e.month,
    returnPct: e.startEquity > 0 ? ((e.endEquity - e.startEquity) / e.startEquity) * 100 : 0,
  }));
}

function returnColor(pct: number): string {
  if (pct >= 10) return "#166534";   // dark green
  if (pct >= 5) return "#15803d";
  if (pct >= 2) return "#22c55e";
  if (pct >= 0) return "#4ade80";
  if (pct >= -2) return "#fca5a5";
  if (pct >= -5) return "#ef4444";
  if (pct >= -10) return "#dc2626";
  return "#991b1b";                   // dark red
}

interface MonthlyReturnsHeatmapProps {
  trial: TrialEquityData;
  height?: number;
}

export function MonthlyReturnsHeatmap({ trial }: MonthlyReturnsHeatmapProps) {
  const canvasRef = useRef<HTMLCanvasElement>(null);

  const { returns, years } = useMemo(() => {
    const r = computeMonthlyReturns(trial);
    const y = [...new Set(r.map((e) => e.year))].sort();
    return { returns: r, years: y };
  }, [trial]);

  const width = LABEL_WIDTH + 12 * CELL_SIZE;
  const height = HEADER_HEIGHT + years.length * CELL_SIZE;

  useEffect(() => {
    const canvas = canvasRef.current;
    if (!canvas || returns.length === 0) return;

    const dpr = window.devicePixelRatio || 1;
    canvas.width = width * dpr;
    canvas.height = height * dpr;
    canvas.style.width = `${width}px`;
    canvas.style.height = `${height}px`;

    const ctx = canvas.getContext("2d");
    if (!ctx) return;
    ctx.scale(dpr, dpr);
    ctx.clearRect(0, 0, width, height);

    // Header: month labels
    ctx.fillStyle = "#9ca0ad";
    ctx.font = "11px monospace";
    ctx.textAlign = "center";
    for (let m = 0; m < 12; m++) {
      ctx.fillText(MONTH_LABELS[m], LABEL_WIDTH + m * CELL_SIZE + CELL_SIZE / 2, 16);
    }

    // Grid
    const returnMap = new Map<string, number>();
    for (const r of returns) returnMap.set(`${r.year}-${r.month}`, r.returnPct);

    for (let yi = 0; yi < years.length; yi++) {
      const y = HEADER_HEIGHT + yi * CELL_SIZE;
      const year = years[yi];

      // Year label
      ctx.fillStyle = "#9ca0ad";
      ctx.textAlign = "right";
      ctx.fillText(String(year), LABEL_WIDTH - 6, y + CELL_SIZE / 2 + 4);

      for (let m = 0; m < 12; m++) {
        const x = LABEL_WIDTH + m * CELL_SIZE;
        const key = `${year}-${m}`;
        const pct = returnMap.get(key);

        if (pct !== undefined) {
          // Cell background
          ctx.fillStyle = returnColor(pct);
          ctx.fillRect(x + 1, y + 1, CELL_SIZE - 2, CELL_SIZE - 2);

          // Value text
          ctx.fillStyle = Math.abs(pct) > 5 ? "#ffffff" : "#e5e7eb";
          ctx.textAlign = "center";
          ctx.font = "10px monospace";
          ctx.fillText(`${pct >= 0 ? "+" : ""}${pct.toFixed(1)}`, x + CELL_SIZE / 2, y + CELL_SIZE / 2 + 4);
        } else {
          // Empty cell
          ctx.fillStyle = "#1a1d27";
          ctx.fillRect(x + 1, y + 1, CELL_SIZE - 2, CELL_SIZE - 2);
        }
      }
    }
  }, [returns, years, width, height]);

  if (returns.length === 0) {
    return (
      <div className="rounded-lg border border-border-default bg-bg-panel p-6 text-center">
        <p className="text-sm text-text-muted">No monthly return data available.</p>
      </div>
    );
  }

  return (
    <div className="space-y-2">
      <h3 className="text-sm font-semibold uppercase tracking-wider text-text-muted">
        Monthly Returns — Best Survivor (Trial {trial.trialIndex})
      </h3>
      <div className="overflow-x-auto rounded-lg border border-border-default bg-bg-panel p-3">
        <canvas ref={canvasRef} />
      </div>
    </div>
  );
}
