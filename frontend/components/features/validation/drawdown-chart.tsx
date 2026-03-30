"use client";

import { useRef, useEffect } from "react";
import type { IChartApi, Time } from "lightweight-charts";
import { createDarkChart, addBaselineSeries, cleanupChart } from "@/lib/utils/chart-utils";
import type { TrialEquityData } from "@/types/validation";

interface DrawdownChartProps {
  trial: TrialEquityData;
  height?: number;
}

function computeDrawdownPct(equity: number[]): number[] {
  const dd = new Array<number>(equity.length);
  let runningMax = equity[0];
  for (let i = 0; i < equity.length; i++) {
    if (equity[i] > runningMax) runningMax = equity[i];
    dd[i] = runningMax > 0 ? ((equity[i] - runningMax) / runningMax) * 100 : 0;
  }
  return dd;
}

export function DrawdownChart({ trial, height = 250 }: DrawdownChartProps) {
  const containerRef = useRef<HTMLDivElement>(null);
  const chartRef = useRef<IChartApi | null>(null);

  useEffect(() => {
    if (!containerRef.current || trial.equity.length === 0) return;

    const chart = createDarkChart(containerRef.current, { height });
    chartRef.current = chart;

    const series = addBaselineSeries(chart, { baseValue: 0, title: "Drawdown %" });

    const drawdown = computeDrawdownPct(trial.equity);
    const data = trial.timestamps.map((ts, i) => ({
      time: Math.floor(ts / 1000) as Time,
      value: drawdown[i],
    }));

    series.setData(data);
    chart.timeScale().fitContent();

    return () => {
      cleanupChart(chart);
      chartRef.current = null;
    };
  }, [trial, height]);

  return (
    <div className="space-y-2">
      <h3 className="text-sm font-semibold uppercase tracking-wider text-text-muted">
        Drawdown — Best Survivor (Trial {trial.trialIndex})
      </h3>
      <div
        ref={containerRef}
        style={{ height }}
        className="w-full rounded-lg overflow-hidden"
      />
    </div>
  );
}
