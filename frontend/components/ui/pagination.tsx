"use client";

interface PaginationProps {
  offset: number;
  limit: number;
  hasMore: boolean;
  totalCount?: number;
  onPageChange: (newOffset: number) => void;
}

export function Pagination({
  offset,
  limit,
  hasMore,
  totalCount,
  onPageChange,
}: PaginationProps) {
  const currentPage = Math.floor(offset / limit) + 1;
  const totalPages =
    totalCount !== undefined ? Math.ceil(totalCount / limit) : undefined;

  const canGoPrevious = offset > 0;
  const canGoNext = hasMore;

  return (
    <div className="flex items-center justify-between py-3">
      <span className="text-sm text-text-secondary">
        {totalPages !== undefined
          ? `Page ${currentPage} of ${totalPages}`
          : `Page ${currentPage}`}
      </span>

      <div className="flex gap-2">
        <button
          type="button"
          disabled={!canGoPrevious}
          onClick={() => onPageChange(Math.max(0, offset - limit))}
          className="rounded-md border border-border-default bg-bg-surface px-3 py-1.5 text-sm text-text-primary transition-colors hover:bg-bg-hover disabled:cursor-not-allowed disabled:opacity-50"
        >
          Previous
        </button>
        <button
          type="button"
          disabled={!canGoNext}
          onClick={() => onPageChange(offset + limit)}
          className="rounded-md border border-border-default bg-bg-surface px-3 py-1.5 text-sm text-text-primary transition-colors hover:bg-bg-hover disabled:cursor-not-allowed disabled:opacity-50"
        >
          Next
        </button>
      </div>
    </div>
  );
}
