// Phase 3 — SI-suffix parser for the new-aggregate form's N input (P3-16). The TRD §3.4
// suffix table:
//   k = 1e3    M = 1e6    G = 1e9
//   m = 1e-3   u = 1e-6
//
// Critical: case matters. Lowercase `m` is milli (1e-3), uppercase `M` is mega (1e6).
// This avoids the EqV_1m_500m ambiguity: "1m" is a timeframe code (1 minute), "500m" is
// 500 milli-units. A case-insensitive parser would silently produce a 1e9× scale error.

const SUFFIX_MULTIPLIER: Record<string, number> = {
  k: 1e3,
  M: 1e6,
  G: 1e9,
  m: 1e-3,
  u: 1e-6,
};

/**
 * Parses an SI-suffixed numeric string into its absolute numeric value.
 *
 * Examples:
 *   parseSi("1k")     -> 1000
 *   parseSi("1.5k")   -> 1500
 *   parseSi("500m")   -> 0.5
 *   parseSi("500M")   -> 500_000_000
 *   parseSi("100")    -> 100        (no suffix is also valid)
 *   parseSi("")       -> throws
 *   parseSi("1.5x")   -> throws     (unknown suffix)
 *   parseSi("abc")    -> throws
 */
export function parseSi(input: string): number {
  if (typeof input !== "string") throw new Error("parseSi: input must be a string");
  const trimmed = input.trim();
  if (trimmed.length === 0) throw new Error("parseSi: empty input");

  // Detect suffix: a single trailing letter that maps in SUFFIX_MULTIPLIER (case-sensitive).
  // Anything else (digits or '.' only) means no suffix.
  const lastChar = trimmed.slice(-1);
  let mantissaText: string;
  let multiplier: number;

  if (lastChar in SUFFIX_MULTIPLIER) {
    mantissaText = trimmed.slice(0, -1);
    multiplier = SUFFIX_MULTIPLIER[lastChar];
  } else {
    mantissaText = trimmed;
    multiplier = 1;
  }

  if (mantissaText.length === 0) {
    throw new Error(`parseSi: missing mantissa in "${input}"`);
  }

  const mantissa = Number(mantissaText);
  if (!Number.isFinite(mantissa)) {
    throw new Error(`parseSi: cannot parse mantissa "${mantissaText}" from "${input}"`);
  }

  return mantissa * multiplier;
}

/** True if `input` is a valid SI-suffixed value (does not throw). Useful for FE input validation. */
export function isValidSi(input: string): boolean {
  try {
    const v = parseSi(input);
    return Number.isFinite(v);
  } catch {
    return false;
  }
}
