"use client";

// Indicator sub-chart: renders line series for a group of indicator buffers
// sharing the same chartId, with an independent Y-axis scale.

import { useRef, useEffect, useImperativeHandle, forwardRef } from "react";
import type { IChartApi, Time } from "lightweight-charts";
import {
  createDarkChart,
  addLineSeries,
  cleanupChart,
  CHART_COLORS,
} from "@/lib/utils/chart-utils";
import type { IndicatorSeries } from "@/types/api";
import type { DebugBufferPoint } from "@/lib/stores/debug-store";

const INDICATOR_COLORS = [
  CHART_COLORS.line1,
  CHART_COLORS.line2,
  CHART_COLORS.line3,
  CHART_COLORS.line4,
];

export interface IndicatorSubChartHandle {
  getChart(): IChartApi | null;
}

interface IndicatorSubChartProps {
  chartId: number;
  // Incremental mode (debug)
  buffers?: Map<string, DebugBufferPoint[]>;
  // Bulk mode (report)
  bulkSeries?: Record<string, IndicatorSeries>;
  height?: number;
}

export const IndicatorSubChart = forwardRef<IndicatorSubChartHandle, IndicatorSubChartProps>(
  function IndicatorSubChart({ chartId, buffers, bulkSeries, height = 150 }, ref) {
    const containerRef = useRef<HTMLDivElement>(null);
    const chartRef = useRef<IChartApi | null>(null);
    const seriesRef = useRef<Map<string, ReturnType<typeof addLineSeries>>>(new Map());

    useImperativeHandle(ref, () => ({
      getChart: () => chartRef.current,
    }));

    // Initialize chart
    useEffect(() => {
      if (!containerRef.current) return;

      const chart = createDarkChart(containerRef.current, { height });
      chartRef.current = chart;
      seriesRef.current = new Map();

      return () => {
        cleanupChart(chart);
        chartRef.current = null;
        seriesRef.current = new Map();
      };
    }, [height]);

    // Bulk mode
    useEffect(() => {
      if (!bulkSeries || !chartRef.current) return;

      let colorIdx = 0;
      for (const [name, series] of Object.entries(bulkSeries)) {
        const lineSeries = addLineSeries(chartRef.current, {
          color: INDICATOR_COLORS[colorIdx % INDICATOR_COLORS.length],
          title: name,
          priceScaleId: `subchart-${chartId}`,
        });
        lineSeries.setData(
          series.points.map((p) => ({ time: p.time as Time, value: p.value }))
        );
        seriesRef.current.set(name, lineSeries);
        colorIdx++;
      }

      chartRef.current.timeScale().fitContent();
    }, [bulkSeries, chartId]);

    // Incremental mode (debug)
    useEffect(() => {
      if (!buffers || !chartRef.current) return;

      let colorIdx = 0;
      for (const [key, points] of buffers) {
        let series = seriesRef.current.get(key);
        if (!series) {
          series = addLineSeries(chartRef.current, {
            color: INDICATOR_COLORS[colorIdx % INDICATOR_COLORS.length],
            title: key,
            priceScaleId: `subchart-${chartId}`,
          });
          seriesRef.current.set(key, series);
        }
        const lineData = points.map((p) => ({
          time: p.time as Time,
          value: p.value,
        }));
        if (lineData.length > 0) {
          series.setData(lineData);
        }
        colorIdx++;
      }
    }, [buffers, chartId]);

    return (
      <div
        ref={containerRef}
        className="w-full rounded-lg overflow-hidden"
        style={{ height }}
      />
    );
  }
);
