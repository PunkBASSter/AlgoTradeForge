"use client";

import { formatNumber } from "@/lib/utils/format";
import type { OrderRow, OrderStatus } from "@/lib/utils/orders";

interface OrderLogProps {
  orders: OrderRow[];
}

const statusClasses: Record<OrderStatus, string> = {
  pending: "bg-yellow-900/30 text-yellow-400 border-yellow-700",
  filled: "bg-green-900/30 text-green-400 border-green-700",
  cancelled: "bg-neutral-800 text-text-muted border-border-default",
  rejected: "bg-red-900/30 text-red-400 border-red-700",
};

function formatHms(ms: number): string {
  const d = new Date(ms);
  const hh = String(d.getUTCHours()).padStart(2, "0");
  const mm = String(d.getUTCMinutes()).padStart(2, "0");
  const ss = String(d.getUTCSeconds()).padStart(2, "0");
  return `${hh}:${mm}:${ss}`;
}

function formatFull(ms: number): string {
  return new Date(ms).toISOString().replace("T", " ").replace("Z", " UTC");
}

export function OrderLog({ orders }: OrderLogProps) {
  return (
    <div className="bg-bg-panel rounded-lg border border-border-default flex flex-col min-h-0">
      <div className="flex items-center justify-between px-4 py-3 border-b border-border-default">
        <h3 className="text-sm font-semibold text-text-secondary">Orders</h3>
        <span className="text-xs text-text-muted">{orders.length}</span>
      </div>
      <div className="overflow-y-auto max-h-[60vh]" data-testid="order-log-scroll">
        <table className="w-full text-xs">
          <thead className="sticky top-0 bg-bg-panel">
            <tr className="border-b border-border-default text-text-muted">
              <th className="px-2 py-2 text-left font-medium uppercase tracking-wider">
                Time
              </th>
              <th className="px-1 py-2 text-center font-medium uppercase tracking-wider">
                Side
              </th>
              <th className="px-2 py-2 text-right font-medium uppercase tracking-wider">
                Price
              </th>
              <th className="px-2 py-2 text-left font-medium uppercase tracking-wider">
                Status
              </th>
              <th className="px-2 py-2 text-left font-medium uppercase tracking-wider">
                Action
              </th>
            </tr>
          </thead>
          <tbody className="divide-y divide-border-subtle">
            {orders.length === 0 ? (
              <tr>
                <td
                  colSpan={5}
                  className="px-4 py-6 text-center text-text-muted"
                >
                  No orders yet
                </td>
              </tr>
            ) : (
              orders.map((o) => {
                const priceValue = o.fillPrice ?? o.placePrice;
                const isFillPrice = o.fillPrice !== null;
                const sideLabel =
                  o.side === "buy" ? "B" : o.side === "sell" ? "S" : "\u2014";
                const sideColor =
                  o.side === "buy"
                    ? "text-accent-green"
                    : o.side === "sell"
                      ? "text-accent-red"
                      : "text-text-muted";
                const priceTitle = isFillPrice
                  ? "Fill price"
                  : o.placePrice !== null
                    ? `Placement price${o.type ? ` (${o.type})` : ""}`
                    : "";
                const actionTitle =
                  o.actionTimeSec !== null
                    ? `${formatFull(o.actionTimeSec * 1000)}${o.reason ? ` \u2014 ${o.reason}` : ""}`
                    : "";
                return (
                  <tr key={o.orderId} className="hover:bg-bg-hover">
                    <td
                      className="px-2 py-1.5 text-text-primary font-mono"
                      title={formatFull(o.placedTimeSec * 1000)}
                    >
                      {formatHms(o.placedTimeSec * 1000)}
                    </td>
                    <td
                      className={`px-1 py-1.5 text-center font-mono font-bold ${sideColor}`}
                      title={o.side ?? "unknown"}
                    >
                      {sideLabel}
                    </td>
                    <td
                      className="px-2 py-1.5 text-right font-mono text-text-primary"
                      title={priceTitle}
                    >
                      {priceValue !== null
                        ? `${formatNumber(priceValue, 2)}${isFillPrice ? "*" : ""}`
                        : "\u2014"}
                    </td>
                    <td className="px-2 py-1.5">
                      <span
                        className={`inline-flex items-center px-1.5 py-0.5 rounded border text-[10px] font-medium ${statusClasses[o.status]}`}
                      >
                        {o.status}
                      </span>
                    </td>
                    <td
                      className="px-2 py-1.5 text-text-primary font-mono"
                      title={actionTitle}
                    >
                      {o.actionTimeSec !== null
                        ? formatHms(o.actionTimeSec * 1000)
                        : "\u2014"}
                    </td>
                  </tr>
                );
              })
            )}
          </tbody>
        </table>
      </div>
    </div>
  );
}
