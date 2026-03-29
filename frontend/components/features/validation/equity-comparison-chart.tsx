"use client";

import { useRef, useEffect } from "react";
import type { IChartApi, Time } from "lightweight-charts";
import { createDarkChart, addLineSeries, cleanupChart, CHART_COLORS } from "@/lib/utils/chart-utils";
import type { TrialEquityData } from "@/types/validation";

const TRIAL_COLORS = [
  CHART_COLORS.line1,
  CHART_COLORS.line2,
  CHART_COLORS.line3,
  CHART_COLORS.line4,
  "#10b981", // emerald
];

interface EquityComparisonChartProps {
  trials: TrialEquityData[];
  height?: number;
}

export function EquityComparisonChart({ trials, height = 350 }: EquityComparisonChartProps) {
  const containerRef = useRef<HTMLDivElement>(null);
  const chartRef = useRef<IChartApi | null>(null);

  useEffect(() => {
    if (!containerRef.current || trials.length === 0) return;

    const chart = createDarkChart(containerRef.current, { height });
    chartRef.current = chart;

    trials.forEach((trial, idx) => {
      const color = TRIAL_COLORS[idx % TRIAL_COLORS.length];
      const series = addLineSeries(chart, {
        color,
        title: `Trial ${trial.trialIndex}`,
        lineWidth: 2,
        lastValueVisible: false,
        priceLineVisible: false,
      });

      const data = trial.timestamps.map((ts, i) => ({
        time: Math.floor(ts / 1000) as Time,
        value: trial.equity[i],
      }));

      series.setData(data);
    });

    chart.timeScale().fitContent();

    return () => {
      cleanupChart(chart);
      chartRef.current = null;
    };
  }, [trials, height]);

  if (trials.length === 0) {
    return (
      <div className="rounded-lg border border-border-default bg-bg-panel p-6 text-center" style={{ height }}>
        <p className="text-sm text-text-muted">No survivor equity data available.</p>
      </div>
    );
  }

  return (
    <div className="space-y-2">
      <h3 className="text-sm font-semibold uppercase tracking-wider text-text-muted">
        Equity Comparison — Top Survivors
      </h3>
      <div
        ref={containerRef}
        style={{ height }}
        className="w-full rounded-lg overflow-hidden"
      />
    </div>
  );
}
