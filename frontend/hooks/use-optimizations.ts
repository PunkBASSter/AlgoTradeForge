// T039 - TanStack Query hook for optimization detail

import { useQuery, useInfiniteQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { getClient } from "@/lib/services";

export function useOptimizationDetail(id: string) {
  const client = getClient();
  return useQuery({
    queryKey: ["optimization", id],
    queryFn: () => client.getOptimization(id),
    enabled: !!id,
  });
}

export function useInfiniteOptimizationTrials(
  id: string,
  options?: { limit?: number; sortBy?: string },
) {
  const client = getClient();
  const limit = options?.limit ?? 100;
  return useInfiniteQuery({
    queryKey: ["optimization", id, "trials", { limit, sortBy: options?.sortBy }],
    queryFn: ({ pageParam = 0 }) =>
      client.getOptimizationTrials(id, { limit, offset: pageParam, sortBy: options?.sortBy }),
    initialPageParam: 0,
    getNextPageParam: (lastPage, _allPages, lastPageParam) =>
      lastPage.hasMore ? lastPageParam + limit : undefined,
    enabled: !!id,
    retry: 2,
  });
}

export function useDeleteOptimization() {
  const client = getClient();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => client.deleteOptimization(id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["optimizations"] });
    },
  });
}
