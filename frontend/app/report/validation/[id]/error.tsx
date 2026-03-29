"use client";

export default function ValidationReportError({
  error,
}: {
  error: Error & { digest?: string };
}) {
  return (
    <div className="p-6 flex flex-col items-center justify-center gap-4">
      <div className="p-6 bg-bg-panel border border-accent-red rounded-lg text-center max-w-md">
        <h2 className="text-lg font-semibold text-accent-red mb-2">
          Failed to load validation
        </h2>
        <p className="text-sm text-text-secondary">{error.message}</p>
      </div>
    </div>
  );
}
