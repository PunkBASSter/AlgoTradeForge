import { describe, it, expect } from "vitest";
import {
  toTitleCase,
  formatNumber,
  formatCurrency,
  formatPercent,
  formatDuration,
} from "./format";

describe("toTitleCase", () => {
  it("converts camelCase to Title Case", () => {
    expect(toTitleCase("sortinoRatio")).toBe("Sortino Ratio");
  });

  it("handles single word", () => {
    expect(toTitleCase("profit")).toBe("Profit");
  });

  it("handles multiple transitions", () => {
    expect(toTitleCase("maxDrawdownPct")).toBe("Max Drawdown Pct");
  });
});

describe("formatNumber", () => {
  it("formats with 2 decimals by default", () => {
    expect(formatNumber(1234.5)).toBe("1,234.50");
  });

  it("formats with custom decimals", () => {
    expect(formatNumber(1234.5678, 3)).toBe("1,234.568");
  });

  it("formats zero", () => {
    expect(formatNumber(0)).toBe("0.00");
  });

  it("formats negative numbers", () => {
    expect(formatNumber(-42.1)).toBe("-42.10");
  });
});

describe("formatCurrency", () => {
  it("formats as USD", () => {
    expect(formatCurrency(1234.5)).toBe("$1,234.50");
  });

  it("formats negative values", () => {
    expect(formatCurrency(-99.99)).toBe("-$99.99");
  });
});

describe("formatPercent", () => {
  it("appends percent sign", () => {
    expect(formatPercent(12.345)).toBe("12.35%");
  });

  it("formats zero", () => {
    expect(formatPercent(0)).toBe("0.00%");
  });
});

describe("formatDuration", () => {
  it("returns ms for sub-second", () => {
    expect(formatDuration(500)).toBe("500ms");
  });

  it("returns seconds only", () => {
    expect(formatDuration(5000)).toBe("5s");
  });

  it("returns minutes and seconds", () => {
    expect(formatDuration(125000)).toBe("2m 5s");
  });

  it("returns hours, minutes, and seconds", () => {
    expect(formatDuration(3661000)).toBe("1h 1m 1s");
  });

  it("handles exact boundary", () => {
    expect(formatDuration(1000)).toBe("1s");
  });
});
