// T010 - TradingView lightweight-charts helpers

import {
  createChart,
  ColorType,
  CandlestickSeries,
  LineSeries,
  BaselineSeries,
  type IChartApi,
  type DeepPartial,
  type ChartOptions,
  type LineWidth,
} from "lightweight-charts";

// ---------------------------------------------------------------------------
// Chart color constants
// ---------------------------------------------------------------------------

export const CHART_COLORS = {
  up: "#22c55e",
  down: "#ef4444",
  line1: "#3b82f6",
  line2: "#a855f7",
  line3: "#eab308",
  line4: "#06b6d4",
  volume: "rgba(59, 130, 246, 0.5)",
} as const;

// ---------------------------------------------------------------------------
// Dark theme chart factory
// ---------------------------------------------------------------------------

export function createDarkChart(
  container: HTMLElement,
  options?: { width?: number; height?: number; autoSize?: boolean },
) {
  const chartOptions: DeepPartial<ChartOptions> = {
    layout: {
      background: { type: ColorType.Solid, color: "#1a1d27" },
      textColor: "#9ca0ad",
    },
    grid: {
      vertLines: { color: "#2e323f" },
      horzLines: { color: "#2e323f" },
    },
    timeScale: {
      timeVisible: true,
      secondsVisible: false,
      borderColor: "#2e323f",
    },
    width: options?.width,
    height: options?.height,
    autoSize: options?.autoSize ?? true,
  };

  return createChart(container, chartOptions);
}

// ---------------------------------------------------------------------------
// Series helpers
// ---------------------------------------------------------------------------

export function addCandlestickSeries(chart: IChartApi) {
  return chart.addSeries(CandlestickSeries, {
    upColor: CHART_COLORS.up,
    downColor: CHART_COLORS.down,
    borderDownColor: CHART_COLORS.down,
    borderUpColor: CHART_COLORS.up,
    wickDownColor: CHART_COLORS.down,
    wickUpColor: CHART_COLORS.up,
  });
}

export function addLineSeries(
  chart: IChartApi,
  options: {
    color: string;
    title?: string;
    priceScaleId?: string;
    lineWidth?: number;
    lineStyle?: number;
    lastValueVisible?: boolean;
    priceLineVisible?: boolean;
  },
) {
  return chart.addSeries(LineSeries, {
    color: options.color,
    title: options.title,
    priceScaleId: options.priceScaleId,
    lineWidth: (options.lineWidth ?? 2) as DeepPartial<LineWidth>,
    lineStyle: options.lineStyle,
    lastValueVisible: options.lastValueVisible,
    priceLineVisible: options.priceLineVisible,
  });
}

export function addBaselineSeries(
  chart: IChartApi,
  options?: { baseValue?: number; title?: string },
) {
  return chart.addSeries(BaselineSeries, {
    baseValue: { type: "price", price: options?.baseValue ?? 0 },
    topLineColor: CHART_COLORS.up,
    topFillColor1: "rgba(34, 197, 94, 0.28)",
    topFillColor2: "rgba(34, 197, 94, 0.05)",
    bottomLineColor: CHART_COLORS.down,
    bottomFillColor1: "rgba(239, 68, 68, 0.05)",
    bottomFillColor2: "rgba(239, 68, 68, 0.28)",
    title: options?.title,
  });
}

// ---------------------------------------------------------------------------
// Cleanup
// ---------------------------------------------------------------------------

export function cleanupChart(chart: IChartApi): void {
  try {
    chart.remove();
  } catch {
    // Chart already removed or container detached
  }
}
