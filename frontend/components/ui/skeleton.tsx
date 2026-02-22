type SkeletonVariant = "line" | "rect" | "chart";

interface SkeletonProps {
  variant?: SkeletonVariant;
  width?: string | number;
  height?: string | number;
  className?: string;
}

const baseClasses = "animate-pulse rounded bg-bg-panel";

export function Skeleton({
  variant = "line",
  width,
  height,
  className = "",
}: SkeletonProps) {
  switch (variant) {
    case "line":
      return (
        <div
          className={`${baseClasses} h-4 ${className}`}
          style={{ width: width ?? "100%" }}
          role="status"
          aria-label="Loading"
        />
      );

    case "rect":
      return (
        <div
          className={`${baseClasses} ${className}`}
          style={{
            width: width ?? "100%",
            height: height ?? "100px",
          }}
          role="status"
          aria-label="Loading"
        />
      );

    case "chart":
      return (
        <div
          className={`${baseClasses} ${className}`}
          style={{
            width: width ?? "100%",
            height: height ?? "400px",
          }}
          role="status"
          aria-label="Loading chart"
        />
      );
  }
}
