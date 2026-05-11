import { describe, it, expect } from "vitest";
import { parseSi, isValidSi } from "./si-suffix";

describe("parseSi", () => {
  it("parses bare numbers without a suffix", () => {
    expect(parseSi("100")).toBe(100);
    expect(parseSi("1.5")).toBe(1.5);
    expect(parseSi("0")).toBe(0);
  });

  it("parses uppercase multipliers (k/M/G)", () => {
    expect(parseSi("1k")).toBe(1_000);
    expect(parseSi("1.5k")).toBe(1_500);
    expect(parseSi("2M")).toBe(2_000_000);
    expect(parseSi("3G")).toBe(3_000_000_000);
  });

  it("parses lowercase fractional multipliers (m/u)", () => {
    expect(parseSi("500m")).toBe(0.5);
    expect(parseSi("100u")).toBeCloseTo(0.0001, 12);
    expect(parseSi("1m")).toBe(0.001);
  });

  it("distinguishes m (milli) from M (mega) — TRD §3.4 case sensitivity", () => {
    // The whole point of the suffix table: case matters. A case-insensitive parser would
    // silently produce a 1e9× scale error on EqV_1m_500m's threshold.
    expect(parseSi("500m")).toBe(0.5);
    expect(parseSi("500M")).toBe(500_000_000);
    expect(parseSi("500m")).not.toBe(parseSi("500M"));
  });

  it("trims surrounding whitespace", () => {
    expect(parseSi("  1k  ")).toBe(1000);
  });

  it("throws on empty input", () => {
    expect(() => parseSi("")).toThrow();
    expect(() => parseSi("   ")).toThrow();
  });

  it("throws on missing mantissa (suffix only)", () => {
    expect(() => parseSi("k")).toThrow();
  });

  it("throws on unknown suffix", () => {
    expect(() => parseSi("1x")).toThrow();
    // 'K' uppercase isn't in the table — TRD §3.4 uses lowercase k for kilo.
    expect(() => parseSi("1K")).toThrow();
  });

  it("throws on non-numeric mantissa", () => {
    expect(() => parseSi("abc")).toThrow();
    expect(() => parseSi("a1k")).toThrow();
  });
});

describe("isValidSi", () => {
  it("returns true for valid inputs", () => {
    expect(isValidSi("100")).toBe(true);
    expect(isValidSi("1.5k")).toBe(true);
    expect(isValidSi("500m")).toBe(true);
  });

  it("returns false for invalid inputs (does not throw)", () => {
    expect(isValidSi("")).toBe(false);
    expect(isValidSi("abc")).toBe(false);
    expect(isValidSi("1x")).toBe(false);
  });
});
