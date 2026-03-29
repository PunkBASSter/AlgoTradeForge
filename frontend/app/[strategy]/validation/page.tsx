"use client";

import React from "react";
import { useValidationList } from "@/hooks/use-validations";
import { ValidationListTable } from "@/components/features/validation/validation-list-table";
import { Pagination } from "@/components/ui/pagination";
import { Skeleton } from "@/components/ui/skeleton";

const LIMIT = 50;

export default function ValidationListPage({
  params,
}: {
  params: Promise<{ strategy: string }>;
}) {
  const { strategy } = React.use(params);
  const [offset, setOffset] = React.useState(0);

  const strategyName = strategy === "all" ? undefined : strategy;

  const { data, isLoading, error } = useValidationList({
    strategyName,
    limit: LIMIT,
    offset,
  });

  if (error) {
    return (
      <div className="p-6">
        <div className="p-4 rounded-lg border border-accent-red bg-red-900/10">
          <p className="text-sm text-accent-red">{error.message}</p>
        </div>
      </div>
    );
  }

  if (isLoading) {
    return (
      <div className="p-6 space-y-4">
        <Skeleton variant="line" width="200px" />
        <Skeleton variant="rect" height="300px" />
      </div>
    );
  }

  return (
    <div className="p-6 space-y-4">
      <h1 className="text-lg font-bold text-text-primary">
        Validation Runs
        {strategyName && (
          <span className="text-text-muted font-normal ml-2">— {strategyName}</span>
        )}
      </h1>

      <ValidationListTable
        validations={data?.items ?? []}
        isLoading={isLoading}
      />

      {data && (
        <Pagination
          offset={offset}
          limit={LIMIT}
          hasMore={data.hasMore}
          totalCount={data.totalCount}
          onPageChange={setOffset}
        />
      )}
    </div>
  );
}
