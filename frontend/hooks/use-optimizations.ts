// T039 - TanStack Query hook for optimization detail

import { useQuery } from "@tanstack/react-query";
import { getClient } from "@/lib/services";

export function useOptimizationDetail(id: string) {
  const client = getClient();
  return useQuery({
    queryKey: ["optimization", id],
    queryFn: () => client.getOptimization(id),
    enabled: !!id,
  });
}
