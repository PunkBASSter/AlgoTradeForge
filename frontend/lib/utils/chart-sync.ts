// Bidirectional time scale sync for multi-pane chart layouts.
// When any chart scrolls/zooms, all others mirror the visible range.

import type { IChartApi, LogicalRange } from "lightweight-charts";

export function syncTimeScales(charts: IChartApi[]): () => void {
  if (charts.length < 2) return () => {};

  let syncing = false;

  const handlers: { chart: IChartApi; handler: (range: LogicalRange | null) => void }[] = [];

  for (const chart of charts) {
    const handler = (range: LogicalRange | null) => {
      if (syncing || !range) return;
      syncing = true;
      try {
        for (const other of charts) {
          if (other !== chart) {
            other.timeScale().setVisibleLogicalRange(range);
          }
        }
      } finally {
        syncing = false;
      }
    };

    chart.timeScale().subscribeVisibleLogicalRangeChange(handler);
    handlers.push({ chart, handler });
  }

  return () => {
    for (const { chart, handler } of handlers) {
      chart.timeScale().unsubscribeVisibleLogicalRangeChange(handler);
    }
  };
}
