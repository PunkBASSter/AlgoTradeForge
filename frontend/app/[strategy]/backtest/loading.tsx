import { Skeleton } from "@/components/ui/skeleton";

export default function BacktestLoading() {
  return (
    <div className="space-y-4">
      <Skeleton variant="line" width="200px" />
      <Skeleton variant="rect" height={40} />
      <Skeleton variant="rect" height={400} />
    </div>
  );
}
