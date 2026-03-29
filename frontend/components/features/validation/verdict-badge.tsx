"use client";

const VERDICT_STYLES: Record<string, string> = {
  Green: "bg-green-500/20 text-green-400 border-green-500/40",
  Yellow: "bg-yellow-500/20 text-yellow-400 border-yellow-500/40",
  Red: "bg-red-500/20 text-red-400 border-red-500/40",
};

interface VerdictBadgeProps {
  verdict: string;
  size?: "sm" | "lg";
}

export function VerdictBadge({ verdict, size = "sm" }: VerdictBadgeProps) {
  const style = VERDICT_STYLES[verdict] ?? VERDICT_STYLES.Red;
  const sizeClass = size === "lg"
    ? "px-4 py-1.5 text-base font-bold"
    : "px-2 py-0.5 text-xs font-semibold";

  return (
    <span className={`inline-flex items-center rounded-full border ${style} ${sizeClass}`}>
      {verdict}
    </span>
  );
}
