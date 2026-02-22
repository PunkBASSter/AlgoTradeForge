// T009 - Formatting utilities

/**
 * Converts a camelCase string to Title Case.
 * e.g. "sortinoRatio" -> "Sortino Ratio", "maxDrawdownPct" -> "Max Drawdown Pct"
 */
export function toTitleCase(camelCase: string): string {
  const spaced = camelCase.replace(/([a-z0-9])([A-Z])/g, "$1 $2");
  return spaced
    .split(" ")
    .map((word) => word.charAt(0).toUpperCase() + word.slice(1))
    .join(" ");
}

/**
 * Formats a number with locale-aware separators.
 * @param value - The number to format
 * @param decimals - Number of decimal places (default 2)
 */
export function formatNumber(value: number, decimals: number = 2): string {
  return value.toLocaleString("en-US", {
    minimumFractionDigits: decimals,
    maximumFractionDigits: decimals,
  });
}

/**
 * Formats a number as USD currency.
 */
export function formatCurrency(value: number): string {
  return value.toLocaleString("en-US", {
    style: "currency",
    currency: "USD",
  });
}

/**
 * Formats a number as a percentage with 2 decimals and % sign.
 */
export function formatPercent(value: number): string {
  return `${value.toLocaleString("en-US", {
    minimumFractionDigits: 2,
    maximumFractionDigits: 2,
  })}%`;
}

/**
 * Formats milliseconds into a human-readable duration string.
 * Returns "Xh Ym Zs", "Xm Zs", "Zs", or "Xms" as appropriate.
 */
export function formatDuration(ms: number): string {
  if (ms < 1000) {
    return `${Math.round(ms)}ms`;
  }

  const totalSeconds = Math.floor(ms / 1000);
  const hours = Math.floor(totalSeconds / 3600);
  const minutes = Math.floor((totalSeconds % 3600) / 60);
  const seconds = totalSeconds % 60;

  if (hours > 0) {
    return `${hours}h ${minutes}m ${seconds}s`;
  }
  if (minutes > 0) {
    return `${minutes}m ${seconds}s`;
  }
  return `${seconds}s`;
}
