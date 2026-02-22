"use client";

// T044 - Backtest report page

import React from "react";
import dynamic from "next/dynamic";
import {
  useBacktestDetail,
  useBacktestEquity,
  useBacktestEvents,
} from "@/hooks/use-backtests";
import { MetricsPanel } from "@/components/features/report/metrics-panel";
import { ParamsPanel } from "@/components/features/report/params-panel";
import { ChartSkeleton } from "@/components/features/charts/chart-skeleton";
import { Skeleton } from "@/components/ui/skeleton";

const EquityChart = dynamic(
  () =>
    import("@/components/features/charts/equity-chart").then(
      (m) => m.EquityChart
    ),
  { ssr: false, loading: () => <ChartSkeleton /> }
);

const CandlestickChart = dynamic(
  () =>
    import("@/components/features/charts/candlestick-chart").then(
      (m) => m.CandlestickChart
    ),
  { ssr: false, loading: () => <ChartSkeleton height={500} /> }
);

export default function BacktestReportPage({
  params,
}: {
  params: Promise<{ id: string }>;
}) {
  const { id } = React.use(params);

  const {
    data: backtest,
    isLoading: backtestLoading,
    error: backtestError,
  } = useBacktestDetail(id);

  const {
    data: equity,
    isLoading: equityLoading,
  } = useBacktestEquity(id);

  const {
    data: events,
    isLoading: eventsLoading,
  } = useBacktestEvents(id, backtest?.hasCandleData ?? false);

  if (backtestError) {
    return (
      <div className="p-6 flex flex-col items-center justify-center gap-4">
        <div className="p-6 bg-bg-panel border border-accent-red rounded-lg text-center max-w-md">
          <h2 className="text-lg font-semibold text-accent-red mb-2">
            Failed to load backtest
          </h2>
          <p className="text-sm text-text-secondary">
            {backtestError.message}
          </p>
        </div>
      </div>
    );
  }

  if (backtestLoading || !backtest) {
    return (
      <div className="p-6 space-y-4">
        <Skeleton variant="line" width="300px" />
        <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
          <Skeleton variant="rect" height="200px" />
          <Skeleton variant="rect" height="200px" />
        </div>
        <Skeleton variant="chart" />
      </div>
    );
  }

  return (
    <div className="p-6 space-y-6">
      {/* Title */}
      <div>
        <h1 className="text-xl font-bold text-text-primary">
          {backtest.strategyName}
          <span className="ml-2 text-sm font-normal text-text-muted">
            v{backtest.strategyVersion}
          </span>
        </h1>
        <p className="text-sm text-text-secondary mt-1">
          {backtest.assetName} / {backtest.exchange} / {backtest.timeFrame}
          {" -- "}
          {new Date(backtest.dataStart).toLocaleDateString()} to{" "}
          {new Date(backtest.dataEnd).toLocaleDateString()}
        </p>
      </div>

      {/* Error display */}
      {backtest.errorMessage && (
        <div className="p-4 rounded-lg border border-accent-red bg-red-900/10 space-y-2">
          <h2 className="text-sm font-semibold text-accent-red">Run Error</h2>
          <p className="text-sm text-text-secondary">{backtest.errorMessage}</p>
          {backtest.errorStackTrace && (
            <pre className="text-xs text-text-muted mt-2 overflow-x-auto whitespace-pre-wrap max-h-48 overflow-y-auto">
              {backtest.errorStackTrace}
            </pre>
          )}
        </div>
      )}

      {/* Metrics & Parameters grid */}
      <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
        <MetricsPanel metrics={backtest.metrics} />
        <ParamsPanel parameters={backtest.parameters} />
      </div>

      {/* Equity chart */}
      <div className="space-y-2">
        <h2 className="text-sm font-semibold uppercase tracking-wider text-text-muted">
          Equity Curve
        </h2>
        {equityLoading ? (
          <ChartSkeleton />
        ) : equity && equity.length > 0 ? (
          <EquityChart data={equity} />
        ) : (
          <div className="rounded-lg border border-border-default bg-bg-panel p-8 text-center text-text-muted">
            No equity data available
          </div>
        )}
      </div>

      {/* Candlestick chart (conditional) */}
      {backtest.hasCandleData && (
        <div className="space-y-2">
          <h2 className="text-sm font-semibold uppercase tracking-wider text-text-muted">
            Price Chart
          </h2>
          {eventsLoading ? (
            <ChartSkeleton height={500} />
          ) : events ? (
            <CandlestickChart
              bulkCandles={events.candles}
              bulkIndicators={events.indicators}
              bulkTrades={events.trades}
              height={500}
            />
          ) : (
            <div className="rounded-lg border border-border-default bg-bg-panel p-8 text-center text-text-muted">
              No candle data available
            </div>
          )}
        </div>
      )}
    </div>
  );
}
