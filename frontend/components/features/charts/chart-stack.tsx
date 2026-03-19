"use client";

// ChartStack: Orchestrates main candlestick chart + indicator sub-charts.
// Partitions indicators by chartId: null → overlay on main, non-null → grouped sub-charts.
// Synchronizes time scales across all chart panes.

import { useRef, useEffect, useMemo } from "react";
import type { IChartApi } from "lightweight-charts";
import {
  CandlestickChart,
  type CandlestickChartHandle,
  type DebugTrade,
} from "./candlestick-chart";
import {
  IndicatorSubChart,
  type IndicatorSubChartHandle,
} from "./indicator-sub-chart";
import { syncTimeScales } from "@/lib/utils/chart-sync";
import type { CandleData, IndicatorSeries, TradeData } from "@/types/api";
import type { DebugBufferPoint, DebugBufferMeta } from "@/lib/stores/debug-store";

interface ChartStackProps {
  // Incremental mode (debug)
  candles?: CandleData[];
  indicatorBuffers?: Map<string, DebugBufferPoint[]>;
  indicatorBufferMeta?: Map<string, DebugBufferMeta>;
  debugTrades?: DebugTrade[];
  // Bulk mode (report)
  bulkCandles?: CandleData[];
  bulkIndicators?: Record<string, IndicatorSeries>;
  bulkTrades?: TradeData[];
  // Common
  height?: number;
}

export function ChartStack({
  candles,
  indicatorBuffers,
  indicatorBufferMeta,
  debugTrades,
  bulkCandles,
  bulkIndicators,
  bulkTrades,
  height = 500,
}: ChartStackProps) {
  const mainRef = useRef<CandlestickChartHandle>(null);
  const subChartRefs = useRef<Map<number, IndicatorSubChartHandle>>(new Map());

  // Compute sub-chart groups from debug mode buffers
  const debugSubCharts = useMemo(() => {
    if (!indicatorBuffers || !indicatorBufferMeta) return new Map<number, Map<string, DebugBufferPoint[]>>();

    const groups = new Map<number, Map<string, DebugBufferPoint[]>>();
    for (const [key, points] of indicatorBuffers) {
      const meta = indicatorBufferMeta.get(key);
      if (!meta || meta.chartId === null) continue;
      let group = groups.get(meta.chartId);
      if (!group) {
        group = new Map();
        groups.set(meta.chartId, group);
      }
      group.set(key, points);
    }
    return groups;
  }, [indicatorBuffers, indicatorBufferMeta]);

  // Compute sub-chart groups from bulk mode indicators
  const bulkSubCharts = useMemo(() => {
    if (!bulkIndicators) return new Map<number, Record<string, IndicatorSeries>>();

    const groups = new Map<number, Record<string, IndicatorSeries>>();
    for (const [name, series] of Object.entries(bulkIndicators)) {
      if (series.chartId == null) continue;
      let group = groups.get(series.chartId);
      if (!group) {
        group = {};
        groups.set(series.chartId, group);
      }
      group[name] = series;
    }
    return groups;
  }, [bulkIndicators]);

  const subChartIds = useMemo(() => {
    const ids = new Set<number>();
    for (const id of debugSubCharts.keys()) ids.add(id);
    for (const id of bulkSubCharts.keys()) ids.add(id);
    return [...ids].sort((a, b) => a - b);
  }, [debugSubCharts, bulkSubCharts]);

  // Sync time scales
  useEffect(() => {
    // Small delay to allow charts to initialize
    const timer = setTimeout(() => {
      const charts: IChartApi[] = [];
      const mainChart = mainRef.current?.getChart();
      if (mainChart) charts.push(mainChart);

      for (const id of subChartIds) {
        const subChart = subChartRefs.current.get(id)?.getChart();
        if (subChart) charts.push(subChart);
      }

      if (charts.length < 2) return;
      const cleanup = syncTimeScales(charts);
      return cleanup;
    }, 100);

    return () => clearTimeout(timer);
  }, [subChartIds, indicatorBuffers, bulkIndicators]);

  return (
    <div className="space-y-1">
      <CandlestickChart
        ref={mainRef}
        candles={candles}
        debugIndicatorBuffers={indicatorBuffers}
        debugIndicatorMeta={indicatorBufferMeta}
        debugTrades={debugTrades}
        bulkCandles={bulkCandles}
        bulkIndicators={bulkIndicators}
        bulkTrades={bulkTrades}
        height={height}
      />
      {subChartIds.map((chartId) => (
        <IndicatorSubChart
          key={chartId}
          ref={(handle) => {
            if (handle) {
              subChartRefs.current.set(chartId, handle);
            } else {
              subChartRefs.current.delete(chartId);
            }
          }}
          chartId={chartId}
          buffers={debugSubCharts.get(chartId)}
          bulkSeries={bulkSubCharts.get(chartId)}
        />
      ))}
    </div>
  );
}
