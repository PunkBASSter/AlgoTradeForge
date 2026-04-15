import type { DataSubscriptionResponse } from "@/types/api";

/** Stacked DSS display: asset name (prominent), exchange + timeframe (muted). */
export function DssCell({ subscription }: { subscription: DataSubscriptionResponse }) {
  return (
    <div className="flex flex-col leading-tight">
      <span className="text-text-primary text-sm font-medium">
        {subscription.assetName}
      </span>
      <span className="text-text-muted text-xs">
        {subscription.exchange}
      </span>
      <span className="text-text-muted text-xs">
        {subscription.timeFrame}
      </span>
    </div>
  );
}
