"use client";

// PnL baseline chart for backtest reports — green above zero, red below

import { useRef, useEffect } from "react";
import type { IChartApi, ISeriesApi, Time } from "lightweight-charts";
import {
  createDarkChart,
  addBaselineSeries,
  cleanupChart,
} from "@/lib/utils/chart-utils";
import type { EquityPoint } from "@/types/api";

type BaselineSeriesApi = ISeriesApi<"Baseline">;

interface BacktestPnlChartProps {
  data: EquityPoint[];
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
    seriesRef.current = addBaselineSeries(chart, { baseValue: 0, title: "PnL" });

    return () => {
      cleanupChart(chart);
      chartRef.current = null;
      seriesRef.current = null;
    };
  }, [height]);

  useEffect(() => {
    if (!chartRef.current || !seriesRef.current || data.length === 0) return;

    const baseEquity = data[0].value;
    const chartData = data.map((pt) => ({
      time: Math.floor(pt.timestampMs / 1000) as Time,
      value: pt.value - baseEquity,
    }));

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
