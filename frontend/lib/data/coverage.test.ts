import { describe, expect, it } from "vitest";
import { findMissingMonths, loadRangeForMonths, monthsInRange } from "./coverage";

describe("monthsInRange", () => {
  it("expands an ISO range to UTC month keys inclusive", () => {
    expect(monthsInRange("2024-01-15T00:00:00Z", "2024-03-02T00:00:00Z"))
      .toEqual(["2024-01", "2024-02", "2024-03"]);
  });
  it("returns empty for inverted ranges", () => {
    expect(monthsInRange("2024-03-01T00:00:00Z", "2024-01-01T00:00:00Z")).toEqual([]);
  });
});

describe("findMissingMonths", () => {
  const now = new Date("2026-07-07T12:00:00Z");
  it("reports uncovered past months only", () => {
    expect(findMissingMonths(["2024-01", "2024-03"], "2024-01-01T00:00:00Z", "2024-03-31T00:00:00Z", now))
      .toEqual(["2024-02"]);
  });
  it("never demands the current month (archive owns closed months only)", () => {
    expect(findMissingMonths([], "2026-06-01T00:00:00Z", "2026-07-07T00:00:00Z", now))
      .toEqual(["2026-06"]);
  });
  it("is empty when everything closed is covered", () => {
    expect(findMissingMonths(["2026-06"], "2026-06-01T00:00:00Z", "2026-07-07T00:00:00Z", now))
      .toEqual([]);
  });
});

describe("loadRangeForMonths", () => {
  it("spans first day of first month to last day of last month", () => {
    expect(loadRangeForMonths(["2024-02", "2024-04"]))
      .toEqual({ from: "2024-02-01", to: "2024-04-30" });
  });
});
