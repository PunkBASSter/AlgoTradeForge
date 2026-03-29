"use client";

import { useRef, useEffect, useMemo } from "react";
import type { ParameterHeatmap } from "@/types/validation";

interface ParameterSurfaceProps {
  heatmap: ParameterHeatmap;
  width?: number;
  height?: number;
}

function interpolateColor(value: number, min: number, max: number): string {
  if (Number.isNaN(value)) return "rgb(40, 40, 40)";
  const t = max > min ? (value - min) / (max - min) : 0.5;

  // Red → Yellow → Green gradient
  const r = Math.round(t < 0.5 ? 220 : 220 - (t - 0.5) * 2 * 180);
  const g = Math.round(t < 0.5 ? t * 2 * 200 : 200);
  const b = Math.round(40);
  return `rgb(${r}, ${g}, ${b})`;
}

export function ParameterSurface({
  heatmap,
  width = 400,
  height = 300,
}: ParameterSurfaceProps) {
  const canvasRef = useRef<HTMLCanvasElement>(null);

  const { fitnessGrid, param1Values, param2Values, param1Name, param2Name, plateauScore } = heatmap;

  const { minFitness, maxFitness } = useMemo(() => {
    let min = Infinity;
    let max = -Infinity;
    for (const row of fitnessGrid) {
      for (const val of row) {
        if (Number.isNaN(val)) continue;
        if (val < min) min = val;
        if (val > max) max = val;
      }
    }
    return { minFitness: min, maxFitness: max };
  }, [fitnessGrid]);

  useEffect(() => {
    const canvas = canvasRef.current;
    if (!canvas) return;
    const ctx = canvas.getContext("2d");
    if (!ctx) return;

    // Scale for HiDPI/Retina displays
    const dpr = window.devicePixelRatio || 1;
    canvas.width = width * dpr;
    canvas.height = height * dpr;
    canvas.style.width = `${width}px`;
    canvas.style.height = `${height}px`;
    ctx.scale(dpr, dpr);

    const margin = { top: 20, right: 20, bottom: 40, left: 60 };
    const plotW = width - margin.left - margin.right;
    const plotH = height - margin.top - margin.bottom;

    ctx.clearRect(0, 0, width, height);
    ctx.fillStyle = "#18181b";
    ctx.fillRect(0, 0, width, height);

    const rows = param1Values.length;
    const cols = param2Values.length;
    const cellW = plotW / cols;
    const cellH = plotH / rows;

    // Draw cells
    for (let i = 0; i < rows; i++) {
      for (let j = 0; j < cols; j++) {
        const val = fitnessGrid[i]?.[j] ?? NaN;
        ctx.fillStyle = interpolateColor(val, minFitness, maxFitness);
        ctx.fillRect(
          margin.left + j * cellW,
          margin.top + i * cellH,
          cellW,
          cellH,
        );
      }
    }

    // Axis labels
    ctx.fillStyle = "#a1a1aa";
    ctx.font = "10px monospace";
    ctx.textAlign = "center";

    // X-axis (param2)
    for (let j = 0; j < cols; j += Math.max(1, Math.floor(cols / 5))) {
      ctx.fillText(
        param2Values[j].toFixed(1),
        margin.left + j * cellW + cellW / 2,
        height - 5,
      );
    }

    // Y-axis (param1)
    ctx.textAlign = "right";
    for (let i = 0; i < rows; i += Math.max(1, Math.floor(rows / 5))) {
      ctx.fillText(
        param1Values[i].toFixed(1),
        margin.left - 5,
        margin.top + i * cellH + cellH / 2 + 3,
      );
    }

    // Axis names
    ctx.fillStyle = "#d4d4d8";
    ctx.font = "11px sans-serif";
    ctx.textAlign = "center";
    ctx.fillText(param2Name, margin.left + plotW / 2, height - 22);

    ctx.save();
    ctx.translate(12, margin.top + plotH / 2);
    ctx.rotate(-Math.PI / 2);
    ctx.fillText(param1Name, 0, 0);
    ctx.restore();
  }, [fitnessGrid, param1Values, param2Values, param1Name, param2Name, width, height, minFitness, maxFitness]);

  return (
    <div className="space-y-1">
      <div className="flex items-center justify-between text-xs text-zinc-400">
        <span>
          {param1Name} vs {param2Name}
        </span>
        <span>Plateau: {(plateauScore * 100).toFixed(0)}%</span>
      </div>
      <canvas
        ref={canvasRef}
        width={width}
        height={height}
        className="rounded border border-zinc-700"
      />
      {/* Color scale legend */}
      <div className="flex items-center gap-2 text-xs text-zinc-500">
        <span className="inline-block w-3 h-3 rounded" style={{ background: interpolateColor(minFitness, minFitness, maxFitness) }} />
        <span>Low</span>
        <div className="flex-1 h-2 rounded" style={{
          background: `linear-gradient(to right, ${interpolateColor(minFitness, minFitness, maxFitness)}, ${interpolateColor((minFitness + maxFitness) / 2, minFitness, maxFitness)}, ${interpolateColor(maxFitness, minFitness, maxFitness)})`,
        }} />
        <span>High</span>
        <span className="inline-block w-3 h-3 rounded" style={{ background: interpolateColor(maxFitness, minFitness, maxFitness) }} />
      </div>
    </div>
  );
}
