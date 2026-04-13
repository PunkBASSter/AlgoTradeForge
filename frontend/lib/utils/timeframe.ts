/** Convert shorthand timeframe (e.g. "1h", "15m", "1d") to .NET TimeSpan format ("01:00:00"). */
export function toTimeSpan(tf: string): string {
  const match = tf.match(/^(\d+)([smhd])$/);
  if (!match) return tf;
  const n = parseInt(match[1], 10);
  switch (match[2]) {
    case "s": return `00:00:${String(n).padStart(2, "0")}`;
    case "m": return `00:${String(n).padStart(2, "0")}:00`;
    case "h": return `${String(n).padStart(2, "0")}:00:00`;
    case "d": return `${n}.00:00:00`;
    default: return tf;
  }
}
