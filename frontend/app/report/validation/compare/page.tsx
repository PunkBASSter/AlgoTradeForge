"use client";

import React from "react";
import { useSearchParams } from "next/navigation";
import { useQueries } from "@tanstack/react-query";
import { getClient } from "@/lib/services";
import { ValidationComparison } from "@/components/features/validation/validation-comparison";
import { Skeleton } from "@/components/ui/skeleton";
import type { ValidationRun } from "@/types/validation";

export default function ValidationComparePage() {
  const searchParams = useSearchParams();
  const idsParam = searchParams.get("ids") ?? "";
  const ids = React.useMemo(
    () => idsParam.split(",").filter(Boolean).slice(0, 4),
    [idsParam],
  );

  const client = getClient();

  const queries = useQueries({
    queries: ids.map((id) => ({
      queryKey: ["validation", id],
      queryFn: () => client.getValidation(id),
    })),
  });

  const isLoading = queries.some((q) => q.isLoading);
  const hasError = queries.some((q) => q.error);
  const validations = queries
    .filter((q) => q.data)
    .map((q) => q.data as ValidationRun);

  if (ids.length < 2) {
    return (
      <div className="p-6 text-center">
        <p className="text-sm text-text-muted">
          Select at least 2 validation runs to compare. Add IDs via ?ids=id1,id2
        </p>
      </div>
    );
  }

  if (isLoading) {
    return (
      <div className="p-6 space-y-4">
        <Skeleton variant="line" width="300px" />
        <Skeleton variant="rect" height="200px" />
        <Skeleton variant="rect" height="300px" />
      </div>
    );
  }

  if (hasError) {
    return (
      <div className="p-6">
        <div className="p-4 rounded-lg border border-accent-red bg-red-900/10">
          <p className="text-sm text-accent-red">Failed to load one or more validation runs.</p>
        </div>
      </div>
    );
  }

  return (
    <div className="p-6 space-y-6">
      <h1 className="text-xl font-bold text-text-primary">
        Validation Comparison
        <span className="ml-2 text-sm font-normal text-text-muted">
          {validations.length} runs
        </span>
      </h1>

      <ValidationComparison validations={validations} />
    </div>
  );
}
