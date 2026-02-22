"use client";

// T040 - EquityChart component using TradingView Lightweight Charts v5

import { useRef, useEffect } from "react";
import type { IChartApi, ISeriesApi, Time } from "lightweight-charts";
import { LineSeries } from "lightweight-charts";
import { createDarkChart, cleanupChart } from "@/lib/utils/chart-utils";
import type { EquityPoint } from "@/types/api";

type LineSeriesApi = ISeriesApi<"Line">;

interface EquityChartProps {
  data: EquityPoint[];
  height?: number;
}

export function EquityChart({ data, height = 400 }: EquityChartProps) {
  const containerRef = useRef<HTMLDivElement>(null);
  const chartRef = useRef<IChartApi | null>(null);
  const seriesRef = useRef<LineSeriesApi | null>(null);

  // Initialize chart
  useEffect(() => {
    if (!containerRef.current) return;

    const chart = createDarkChart(containerRef.current, { height });
    chartRef.current = chart;

    const series = chart.addSeries(LineSeries, {
      color: "#3b82f6",
      title: "Equity",
      lineWidth: 2,
    });
    seriesRef.current = series;

    return () => {
      cleanupChart(chart);
      chartRef.current = null;
      seriesRef.current = null;
    };
  }, [height]);

  // Update data
  useEffect(() => {
    if (!chartRef.current || !seriesRef.current || data.length === 0) return;

    const chartData = data.map((point) => ({
      time: Math.floor(point.timestampMs / 1000) as Time,
      value: point.value,
    }));

    seriesRef.current.setData(chartData);
    chartRef.current.timeScale().fitContent();
  }, [data]);

  return (
    <div
      ref={containerRef}
      style={{ height }}
      className="w-full rounded-lg overflow-hidden"
    />
  );
}
