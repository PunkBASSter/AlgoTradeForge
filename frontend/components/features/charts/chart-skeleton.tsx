export function ChartSkeleton({ height = 400 }: { height?: number }) {
  return (
    <div
      className="w-full rounded-lg bg-bg-panel animate-pulse"
      style={{ height }}
    />
  );
}
