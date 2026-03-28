// Shared session-storage keys used for cross-page state transfer
export const SESSION_KEYS = {
  RERUN_BACKTEST: "rerun-backtest-config",
  RERUN_OPTIMIZATION: "rerun-optimization-config",
  DEBUG_CONFIG: "debug-session-config",
  DEBUG_AUTOSTART: "debug-session-autostart",
} as const;
