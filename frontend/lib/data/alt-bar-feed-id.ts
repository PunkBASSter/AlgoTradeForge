// Reviewer Issue F1 — canonical parser for the alt-bar feed-id grammar (TRD §3.3),
// mirroring the C# `AltBarFeedId.TryParse` in
// `src/AlgoTradeForge.HistoryLoader.Domain/AltBarFeedId.cs`. Both sides use a positional
// 3-component split on `_` plus an optional trailing `.flow` sidecar suffix:
//
//   <TypeCode>_<SourceCode>_<Threshold>[.flow]
//
// Replaces the prior ad-hoc `id.split("_")[1]`, which silently broke if the grammar ever
// grew a fourth segment (e.g. multi-segment SourceCode). Keep both parsers in lockstep:
// when the C# allowed sets change, mirror them here.

export const ALLOWED_TYPE_CODES = new Set([
  "EqT", "EqV", "EqD", "EqI", "Range", "Renko",
] as const);

export const ALLOWED_SOURCE_CODES = new Set([
  "1m", "3m", "5m", "15m", "30m",
  "1h", "2h", "4h", "6h", "8h", "12h",
  "1d", "ticks",
] as const);

export interface AltBarFeedIdParts {
  typeCode: string;
  sourceCode: string;
  threshold: string;
  isSidecar: boolean;
}

export function parseAltBarFeedId(text: string): AltBarFeedIdParts | null {
  if (!text) return null;

  let raw = text;
  let isSidecar = false;
  if (raw.endsWith(".flow")) {
    isSidecar = true;
    raw = raw.slice(0, -".flow".length);
  }

  const parts = raw.split("_");
  if (parts.length !== 3) return null;

  const [typeCode, sourceCode, threshold] = parts;
  if (!(ALLOWED_TYPE_CODES as ReadonlySet<string>).has(typeCode)) return null;
  if (!(ALLOWED_SOURCE_CODES as ReadonlySet<string>).has(sourceCode)) return null;
  if (threshold.length === 0) return null;

  return { typeCode, sourceCode, threshold, isSidecar };
}
