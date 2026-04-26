import type { DebugTrade } from "@/components/features/charts/candlestick-chart";
import type {
  OrderCancelEventData,
  OrderFillEventData,
  OrderPlaceEventData,
  OrderRejectEventData,
} from "@/lib/events/types";

export type OrderStatus = "pending" | "filled" | "cancelled" | "rejected";
export type OrderSide = "buy" | "sell";
export type OrderTypeName = "market" | "limit" | "stop" | "stopLimit";

export interface OrderRow {
  orderId: number;
  placedTimeSec: number;
  side: OrderSide | null;
  type: OrderTypeName | null;
  placePrice: number | null;
  fillPrice: number | null;
  status: OrderStatus;
  actionTimeSec: number | null;
  reason?: string;
}

export function reduceOrders(trades: DebugTrade[]): OrderRow[] {
  const rows = new Map<number, OrderRow>();

  for (const t of trades) {
    if (t.type === "ord.place") {
      const d = t.data as OrderPlaceEventData;
      const existing = rows.get(d.orderId);
      if (existing) {
        existing.placedTimeSec = t.time;
        existing.side = d.side;
        existing.type = d.type;
        existing.placePrice = d.stopPrice ?? d.limitPrice ?? null;
      } else {
        rows.set(d.orderId, {
          orderId: d.orderId,
          placedTimeSec: t.time,
          side: d.side,
          type: d.type,
          placePrice: d.stopPrice ?? d.limitPrice ?? null,
          fillPrice: null,
          status: "pending",
          actionTimeSec: null,
        });
      }
    } else if (t.type === "ord.fill") {
      const d = t.data as OrderFillEventData;
      const row = rows.get(d.orderId);
      if (row) {
        row.fillPrice = d.price;
        row.side = row.side ?? d.side;
        row.status = "filled";
        row.actionTimeSec = t.time;
      } else {
        rows.set(d.orderId, {
          orderId: d.orderId,
          placedTimeSec: t.time,
          side: d.side,
          type: null,
          placePrice: null,
          fillPrice: d.price,
          status: "filled",
          actionTimeSec: t.time,
        });
      }
    } else if (t.type === "ord.cancel") {
      const d = t.data as OrderCancelEventData;
      const row = rows.get(d.orderId);
      if (row) {
        row.status = "cancelled";
        row.actionTimeSec = t.time;
        row.reason = d.reason;
      } else {
        rows.set(d.orderId, {
          orderId: d.orderId,
          placedTimeSec: t.time,
          side: null,
          type: null,
          placePrice: null,
          fillPrice: null,
          status: "cancelled",
          actionTimeSec: t.time,
          reason: d.reason,
        });
      }
    } else if (t.type === "ord.reject") {
      const d = t.data as OrderRejectEventData;
      const row = rows.get(d.orderId);
      if (row) {
        row.status = "rejected";
        row.actionTimeSec = t.time;
        row.reason = d.reason;
      } else {
        rows.set(d.orderId, {
          orderId: d.orderId,
          placedTimeSec: t.time,
          side: null,
          type: null,
          placePrice: null,
          fillPrice: null,
          status: "rejected",
          actionTimeSec: t.time,
          reason: d.reason,
        });
      }
    }
  }

  return Array.from(rows.values()).sort(
    (a, b) => b.placedTimeSec - a.placedTimeSec,
  );
}
