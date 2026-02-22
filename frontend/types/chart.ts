// T006 - Chart-specific types

export interface ChartConfig {
  width?: number;
  height?: number;
  autoSize?: boolean;
}

export interface SeriesConfig {
  name: string;
  color: string;
  priceScaleId?: string;
  visible?: boolean;
}

export type MarkerShape = "circle" | "square" | "arrowUp" | "arrowDown";
export type MarkerPosition = "aboveBar" | "belowBar" | "inBar";

export interface ChartMarker {
  time: number;
  position: MarkerPosition;
  color: string;
  shape: MarkerShape;
  text: string;
}
