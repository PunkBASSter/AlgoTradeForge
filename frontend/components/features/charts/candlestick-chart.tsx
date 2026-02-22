"use client";

// T024 + T043 - CandlestickChart with incremental (debug) and bulk (report) modes

import { useRef, useEffect } from "react";
import type { IChartApi, ISeriesMarkersPluginApi, SeriesMarker, Time } from "lightweight-charts";
import { createSeriesMarkers } from "lightweight-charts";
import {
  createDarkChart,
  addCandlestickSeries,
  addLineSeries,
  cleanupChart,
  CHART_COLORS,
} from "@/lib/utils/chart-utils";
import type { CandleData, IndicatorSeries, TradeData } from "@/types/api";
import type {
  OrderPlaceEventData,
  OrderFillEventData,
  PositionEventData,
} from "@/lib/events/types";

const INDICATOR_COLORS = [
  CHART_COLORS.line1,
  CHART_COLORS.line2,
  CHART_COLORS.line3,
  CHART_COLORS.line4,
];

export interface DebugTrade {
  time: number;
  type: "ord.place" | "ord.fill" | "pos";
  data: OrderPlaceEventData | OrderFillEventData | PositionEventData;
}

interface DebugIndicatorPoint {
  time: number;
  indicatorName: string;
  measure: string;
  values: Record<string, number | null>;
}

interface CandlestickChartProps {
  // Incremental mode (debug)
  candles?: CandleData[];
  debugIndicators?: Map<string, DebugIndicatorPoint[]>;
  debugTrades?: DebugTrade[];
  // Bulk mode (report)
  bulkCandles?: CandleData[];
  bulkIndicators?: Record<string, IndicatorSeries>;
  bulkTrades?: TradeData[];
  // Common
  height?: number;
}

function getTradeMarkerSide(data: OrderPlaceEventData | OrderFillEventData | PositionEventData): "buy" | "sell" {
  if ("side" in data) return data.side;
  return "buy";
}

export function CandlestickChart({
  candles,
  debugIndicators,
  debugTrades,
  bulkCandles,
  bulkIndicators,
  bulkTrades,
  height = 500,
}: CandlestickChartProps) {
  const containerRef = useRef<HTMLDivElement>(null);
  const chartRef = useRef<IChartApi | null>(null);
  const candleSeriesRef = useRef<ReturnType<typeof addCandlestickSeries> | null>(null);
  const indicatorSeriesRef = useRef<Map<string, ReturnType<typeof addLineSeries>>>(new Map());
  const markersRef = useRef<ISeriesMarkersPluginApi<Time> | null>(null);
  const lastCandleCountRef = useRef(0);
  const lastTradeCountRef = useRef(0);

  // Initialize chart
  useEffect(() => {
    if (!containerRef.current) return;

    const chart = createDarkChart(containerRef.current, { height });
    chartRef.current = chart;
    candleSeriesRef.current = addCandlestickSeries(chart);
    indicatorSeriesRef.current = new Map();
    lastCandleCountRef.current = 0;
    lastTradeCountRef.current = 0;

    return () => {
      if (markersRef.current) {
        markersRef.current.detach();
        markersRef.current = null;
      }
      cleanupChart(chart);
      chartRef.current = null;
      candleSeriesRef.current = null;
      indicatorSeriesRef.current = new Map();
    };
  }, [height]);

  // Bulk mode: load all data at once (report screen)
  useEffect(() => {
    if (!bulkCandles || !chartRef.current || !candleSeriesRef.current) return;

    candleSeriesRef.current.setData(
      bulkCandles.map((c) => ({
        time: c.time as Time,
        open: c.open,
        high: c.high,
        low: c.low,
        close: c.close,
      }))
    );

    // Bulk indicators
    if (bulkIndicators) {
      let colorIdx = 0;
      for (const [name, series] of Object.entries(bulkIndicators)) {
        const priceScaleId = series.measure === "price" ? "right" : name;
        const lineSeries = addLineSeries(chartRef.current, {
          color: INDICATOR_COLORS[colorIdx % INDICATOR_COLORS.length],
          title: name,
          priceScaleId,
        });
        lineSeries.setData(
          series.points.map((p) => ({ time: p.time as Time, value: p.value }))
        );
        indicatorSeriesRef.current.set(name, lineSeries);
        colorIdx++;
      }
    }

    // Bulk trades as markers
    if (bulkTrades && bulkTrades.length > 0) {
      const markers: SeriesMarker<Time>[] = [];
      for (const trade of bulkTrades) {
        markers.push({
          time: trade.entryTime as Time,
          position: trade.side === "buy" ? "belowBar" : "aboveBar",
          color: trade.side === "buy" ? CHART_COLORS.up : CHART_COLORS.down,
          shape: trade.side === "buy" ? "arrowUp" : "arrowDown",
          text: `${trade.side.toUpperCase()} ${trade.quantity}`,
        });
        if (trade.exitTime) {
          markers.push({
            time: trade.exitTime as Time,
            position: trade.side === "buy" ? "aboveBar" : "belowBar",
            color: trade.side === "buy" ? CHART_COLORS.down : CHART_COLORS.up,
            shape: trade.side === "buy" ? "arrowDown" : "arrowUp",
            text: `CLOSE ${trade.pnl != null ? (trade.pnl >= 0 ? "+" : "") + trade.pnl.toFixed(2) : ""}`,
          });
        }
      }
      markers.sort((a, b) => (a.time as number) - (b.time as number));

      if (markersRef.current) markersRef.current.detach();
      markersRef.current = createSeriesMarkers(candleSeriesRef.current, markers);

      // TP/SL horizontal lines for report mode
      for (const trade of bulkTrades) {
        if (trade.takeProfitPrice != null && trade.entryTime && trade.exitTime) {
          const tpLine = addLineSeries(chartRef.current, {
            color: CHART_COLORS.up,
            priceScaleId: "right",
            lineWidth: 1,
          });
          tpLine.setData([
            { time: trade.entryTime as Time, value: trade.takeProfitPrice },
            { time: trade.exitTime as Time, value: trade.takeProfitPrice },
          ]);
        }
        if (trade.stopLossPrice != null && trade.entryTime && trade.exitTime) {
          const slLine = addLineSeries(chartRef.current, {
            color: CHART_COLORS.down,
            priceScaleId: "right",
            lineWidth: 1,
          });
          slLine.setData([
            { time: trade.entryTime as Time, value: trade.stopLossPrice },
            { time: trade.exitTime as Time, value: trade.stopLossPrice },
          ]);
        }
      }
    }

    chartRef.current.timeScale().fitContent();
  }, [bulkCandles, bulkIndicators, bulkTrades]);

  // Incremental mode: update candles one by one (debug screen)
  useEffect(() => {
    if (!candles || !candleSeriesRef.current) return;

    const newCandles = candles.slice(lastCandleCountRef.current);
    for (const c of newCandles) {
      candleSeriesRef.current.update({
        time: c.time as Time,
        open: c.open,
        high: c.high,
        low: c.low,
        close: c.close,
      });
    }
    lastCandleCountRef.current = candles.length;
  }, [candles]);

  // Incremental indicators
  useEffect(() => {
    if (!debugIndicators || !chartRef.current) return;

    let colorIdx = 0;
    for (const [name, points] of debugIndicators) {
      let series = indicatorSeriesRef.current.get(name);
      if (!series) {
        const measure = points[0]?.measure ?? "price";
        const priceScaleId = measure === "price" ? "right" : name;
        series = addLineSeries(chartRef.current, {
          color: INDICATOR_COLORS[colorIdx % INDICATOR_COLORS.length],
          title: name,
          priceScaleId,
        });
        indicatorSeriesRef.current.set(name, series);
      }
      const lineData = points.flatMap((p) =>
        Object.entries(p.values)
          .filter(([, v]) => v != null)
          .map(([, v]) => ({ time: p.time as Time, value: v as number }))
      );
      if (lineData.length > 0) {
        series.setData(lineData);
      }
      colorIdx++;
    }
  }, [debugIndicators]);

  // Incremental trade markers
  useEffect(() => {
    if (!debugTrades || !candleSeriesRef.current) return;
    if (debugTrades.length === lastTradeCountRef.current) return;

    const markers: SeriesMarker<Time>[] = debugTrades.map((t) => {
      const side = getTradeMarkerSide(t.data);
      const isBuy = side === "buy";
      return {
        time: t.time as Time,
        position: isBuy ? "belowBar" : "aboveBar",
        color: isBuy ? CHART_COLORS.up : CHART_COLORS.down,
        shape: t.type === "ord.place" ? "circle" : isBuy ? "arrowUp" : "arrowDown",
        text: t.type === "ord.place" ? "ORD" : t.type === "ord.fill" ? "FILL" : "POS",
      };
    });
    markers.sort((a, b) => (a.time as number) - (b.time as number));

    if (markersRef.current) markersRef.current.detach();
    markersRef.current = createSeriesMarkers(candleSeriesRef.current, markers);
    lastTradeCountRef.current = debugTrades.length;
  }, [debugTrades]);

  return (
    <div
      ref={containerRef}
      className="w-full rounded-lg overflow-hidden"
      style={{ height }}
    />
  );
}
