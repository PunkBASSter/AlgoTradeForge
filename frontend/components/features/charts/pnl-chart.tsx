"use client";

// PnL chart component — baseline series (green above / red below zero)

import { useRef, useEffect } from "react";
import type { IChartApi, ISeriesApi, Time } from "lightweight-charts";
import {
  createDarkChart,
  addBaselineSeries,
  cleanupChart,
} from "@/lib/utils/chart-utils";
import type { EquityPoint } from "@/lib/stores/debug-store";

type BaselineSeriesApi = ISeriesApi<"Baseline">;

interface PnlChartProps {
  equityHistory: EquityPoint[];
  height?: number;
}

export function PnlChart({
  equityHistory,
  height = 200,
}: PnlChartProps) {
  const containerRef = useRef<HTMLDivElement>(null);
  const chartRef = useRef<IChartApi | null>(null);
  const seriesRef = useRef<BaselineSeriesApi | null>(null);
  const lastPointCountRef = useRef(0);

  // Initialize chart
  useEffect(() => {
    if (!containerRef.current) return;

    const chart = createDarkChart(containerRef.current, { height });
    chartRef.current = chart;
    seriesRef.current = addBaselineSeries(chart, { baseValue: 0, title: "PnL" });
    lastPointCountRef.current = 0;

    return () => {
      cleanupChart(chart);
      chartRef.current = null;
      seriesRef.current = null;
    };
  }, [height]);

  // Incremental PnL data updates
  useEffect(() => {
    if (!seriesRef.current || equityHistory.length === 0) return;

    const baseEquity = equityHistory[0].equity;
    const newPoints = equityHistory.slice(lastPointCountRef.current);
    for (const pt of newPoints) {
      seriesRef.current.update({
        time: pt.time as Time,
        value: pt.equity - baseEquity,
      });
    }
    lastPointCountRef.current = equityHistory.length;
  }, [equityHistory]);

  return (
    <div
      ref={containerRef}
      className="w-full rounded-lg overflow-hidden"
      style={{ height }}
    />
  );
}
