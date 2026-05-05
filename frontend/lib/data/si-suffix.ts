// SI-suffix parser. Suffix table: k=1e3, M=1e6, G=1e9, m=1e-3, u=1e-6.
//
// Case is significant: `m` is milli, `M` is mega. Required to disambiguate
// EqV_1m_500m where "1m" is a timeframe (1 minute) and "500m" is 500 milli-units.
// A case-insensitive parser would silently introduce a 1e9× scale error.

const SUFFIX_MULTIPLIER: Record<string, number> = {
  k: 1e3,
  M: 1e6,
  G: 1e9,
  m: 1e-3,
  u: 1e-6,
};

/**
 * Parses an SI-suffixed numeric string. Throws on empty/unknown-suffix/non-numeric.
 *
 *   parseSi("1.5k") -> 1500    parseSi("500m") -> 0.5    parseSi("100") -> 100
 */
export function parseSi(input: string): number {
  if (typeof input !== "string") throw new Error("parseSi: input must be a string");
  const trimmed = input.trim();
  if (trimmed.length === 0) throw new Error("parseSi: empty input");

  // Suffix is a single trailing letter present in SUFFIX_MULTIPLIER (case-sensitive).
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

/** True if `input` is a valid SI-suffixed value (does not throw). */
export function isValidSi(input: string): boolean {
  try {
    const v = parseSi(input);
    return Number.isFinite(v);
  } catch {
    return false;
  }
}
