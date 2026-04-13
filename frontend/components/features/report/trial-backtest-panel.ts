// T048 - Helper to open a trial as a new backtest via RunNewPanel

import type { BacktestRun, RunBacktestRequest } from "@/types/api";
import { toTimeSpan } from "@/lib/utils/timeframe";

const INTERNAL_PARAM_KEYS = new Set(["DataSubscriptions"]);

/**
 * Constructs a RunBacktestRequest from a trial's data and opens the RunNewPanel
 * via the RunNewContext's openWithContent callback.
 */
export function openTrialAsBacktest(
  trial: BacktestRun,
  openWithContent: (content: Record<string, unknown>) => void,
): void {
  const request: RunBacktestRequest = {
    strategyName: trial.strategyName,
    dataSubscriptions: trial.dataSubscriptions.map((ds) => ({
      assetName: ds.assetName,
      exchange: ds.exchange,
      timeFrame: toTimeSpan(ds.timeFrame),
    })),
    backtestSettings: {
      initialCash: trial.backtestSettings.initialCash,
      startTime: trial.backtestSettings.startTime,
      endTime: trial.backtestSettings.endTime,
      commissionPerTrade: trial.backtestSettings.commissionPerTrade,
      slippageTicks: trial.backtestSettings.slippageTicks,
    },
    strategyParameters: Object.fromEntries(
      Object.entries(trial.parameters).filter(
        ([k]) => !INTERNAL_PARAM_KEYS.has(k),
      ),
    ),
  };

  openWithContent(request as unknown as Record<string, unknown>);
}
