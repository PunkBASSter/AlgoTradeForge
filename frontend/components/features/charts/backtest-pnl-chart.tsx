"use client";

// Accumulated (cumulative) realized PnL chart — baseline series (green above zero, red below)

import { useRef, useEffect } from "react";
import type { IChartApi, ISeriesApi, Time } from "lightweight-charts";
import {
  createDarkChart,
  addBaselineSeries,
  cleanupChart,
} from "@/lib/utils/chart-utils";
import type { TradePoint } from "@/types/api";

type BaselineSeriesApi = ISeriesApi<"Baseline">;

interface BacktestPnlChartProps {
  data: TradePoint[];
  height?: number;
}

export function BacktestPnlChart({ data, height = 300 }: BacktestPnlChartProps) {
  const containerRef = useRef<HTMLDivElement>(null);
  const chartRef = useRef<IChartApi | null>(null);
  const seriesRef = useRef<BaselineSeriesApi | null>(null);

  useEffect(() => {
    if (!containerRef.current) return;

    const chart = createDarkChart(containerRef.current, { height });
    chartRef.current = chart;
    seriesRef.current = addBaselineSeries(chart, { title: "Accumulated PnL" });

    return () => {
      cleanupChart(chart);
      chartRef.current = null;
      seriesRef.current = null;
    };
  }, [height]);

  useEffect(() => {
    if (!chartRef.current || !seriesRef.current || data.length === 0) return;

    // Aggregate PnL by timestamp (multiple trades may close on the same bar)
    const aggregated = new Map<number, number>();
    for (const pt of data) {
      const ts = Math.floor(pt.timestampMs / 1000);
      aggregated.set(ts, (aggregated.get(ts) ?? 0) + pt.pnl);
    }

    // Compute running sum for cumulative PnL
    let cumulative = 0;
    const chartData = Array.from(aggregated.entries())
      .sort(([a], [b]) => a - b)
      .map(([ts, pnl]) => {
        cumulative += pnl;
        return { time: ts as Time, value: cumulative };
      });

    seriesRef.current.setData(chartData);
    chartRef.current.timeScale().fitContent();
  }, [data]);

  return (
    <div
      ref={containerRef}
      className="w-full rounded-lg overflow-hidden"
      style={{ height }}
    />
  );
}
