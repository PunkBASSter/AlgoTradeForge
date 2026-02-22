// T005 - Backtest event types (matching data-model.md)

export type EventType =
  | "bar" | "bar.mut"
  | "ind" | "ind.mut"
  | "sig" | "risk"
  | "ord.place" | "ord.fill" | "ord.cancel" | "ord.reject"
  | "pos"
  | "run.start" | "run.end"
  | "err" | "warn";

export interface BacktestEvent<T = unknown> {
  ts: string;
  sq: number;
  _t: EventType;
  src: string;
  d: T;
}

// bar, bar.mut
export interface BarEventData {
  assetName: string;
  timeFrame: string;
  open: number;
  high: number;
  low: number;
  close: number;
  volume: number;
}

// ind, ind.mut
export interface IndicatorEventData {
  indicatorName: string;
  measure: "price" | "percent" | "minusOnePlusOne" | "volume";
  values: Record<string, number | null>;
}

// sig
export interface SignalEventData {
  signalName: string;
  assetName: string;
  direction: string;
  strength: number;
  reason?: string;
}

// risk
export interface RiskEventData {
  assetName: string;
  passed: boolean;
  checkName: string;
  reason?: string;
}

// ord.place
export interface OrderPlaceEventData {
  orderId: number;
  assetName: string;
  side: "buy" | "sell";
  type: "market" | "limit" | "stop" | "stopLimit";
  quantity: number;
  limitPrice?: number;
  stopPrice?: number;
}

// ord.fill
export interface OrderFillEventData {
  orderId: number;
  assetName: string;
  side: "buy" | "sell";
  price: number;
  quantity: number;
  commission: number;
}

// ord.cancel
export interface OrderCancelEventData {
  orderId: number;
  assetName: string;
  reason?: string;
}

// ord.reject
export interface OrderRejectEventData {
  orderId: number;
  assetName: string;
  reason: string;
}

// pos
export interface PositionEventData {
  assetName: string;
  quantity: number;
  averageEntryPrice: number;
  realizedPnl: number;
}

// run.start
export interface RunStartEventData {
  strategyName: string;
  assetName: string;
  initialCash: number;
  startTime: string;
  endTime: string;
  runMode: string;
}

// run.end
export interface RunEndEventData {
  totalBarsProcessed: number;
  finalEquity: number;
  totalFills: number;
  duration: string;
}

// err
export interface ErrorEventData {
  message: string;
  stackTrace?: string;
}

// warn
export interface WarningEventData {
  message: string;
}
