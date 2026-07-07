// Month math for archive coverage. All computations are UTC; month keys are "yyyy-MM".
// The archive covers CLOSED months only — the current UTC month is REST-tail-owned and
// must never be reported missing, or load banners would never clear.

export function monthsInRange(fromIso: string, toIso: string): string[] {
  const from = new Date(fromIso);
  const to = new Date(toIso);
  if (Number.isNaN(from.getTime()) || Number.isNaN(to.getTime()) || from > to) return [];
  const months: string[] = [];
  let y = from.getUTCFullYear();
  let m = from.getUTCMonth();
  const endY = to.getUTCFullYear();
  const endM = to.getUTCMonth();
  while (y < endY || (y === endY && m <= endM)) {
    months.push(`${y}-${String(m + 1).padStart(2, "0")}`);
    m += 1;
    if (m === 12) { m = 0; y += 1; }
  }
  return months;
}

export function findMissingMonths(
  covered: readonly string[],
  fromIso: string,
  toIso: string,
  now: Date = new Date(),
): string[] {
  const currentMonth = `${now.getUTCFullYear()}-${String(now.getUTCMonth() + 1).padStart(2, "0")}`;
  const coveredSet = new Set(covered);
  return monthsInRange(fromIso, toIso).filter((m) => m < currentMonth && !coveredSet.has(m));
}

export function loadRangeForMonths(months: readonly string[]): { from: string; to: string } {
  const first = months[0];
  const last = months[months.length - 1];
  const [y, m] = last.split("-").map(Number);
  const lastDay = new Date(Date.UTC(y, m, 0)).getUTCDate(); // day 0 of next month = last day
  return { from: `${first}-01`, to: `${last}-${String(lastDay).padStart(2, "0")}` };
}
