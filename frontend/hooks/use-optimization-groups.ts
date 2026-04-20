// T041 - TanStack Query hooks for optimization groups

import { useQuery, useInfiniteQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { getClient } from "@/lib/services";
import type { OptimizationGroupListParams } from "@/lib/services/api-client";

export function useOptimizationGroups(params: OptimizationGroupListParams) {
  const client = getClient();
  return useQuery({
    queryKey: ["optimization-groups", params],
    queryFn: () => client.getOptimizationGroups(params),
  });
}

export function useOptimizationGroupDetail(groupId: string) {
  const client = getClient();
  return useQuery({
    queryKey: ["optimization-group", groupId],
    queryFn: () => client.getOptimizationGroup(groupId),
    enabled: !!groupId,
  });
}

export function useOptimizationGroupStatus(groupId: string) {
  const client = getClient();
  return useQuery({
    queryKey: ["optimization-group-status", groupId],
    queryFn: () => client.getOptimizationGroupStatus(groupId),
    enabled: !!groupId,
    refetchInterval: (query) => {
      const status = query.state.data?.status;
      if (status === "Completed" || status === "Failed" || status === "Cancelled")
        return false;
      return 2000;
    },
  });
}

export function useInfiniteOptimizationGroupTrials(
  groupId: string,
  options?: { limit?: number; sortBy?: string },
) {
  const client = getClient();
  const limit = options?.limit ?? 100;
  return useInfiniteQuery({
    queryKey: ["optimization-group", groupId, "trials", { limit, sortBy: options?.sortBy }],
    queryFn: ({ pageParam = 0 }) =>
      client.getOptimizationGroupTrials(groupId, { limit, offset: pageParam, sortBy: options?.sortBy }),
    initialPageParam: 0,
    getNextPageParam: (lastPage, _allPages, lastPageParam) =>
      lastPage.hasMore ? lastPageParam + limit : undefined,
    enabled: !!groupId,
    retry: 2,
  });
}

export function useCancelOptimizationGroup() {
  const client = getClient();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (groupId: string) => client.cancelOptimizationGroup(groupId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["task-queue"] });
      queryClient.invalidateQueries({ queryKey: ["optimizations"] });
      queryClient.invalidateQueries({ queryKey: ["optimization-groups"] });
      queryClient.invalidateQueries({ queryKey: ["optimization-group"] });
    },
  });
}

export function useDeleteOptimizationGroup() {
  const client = getClient();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (groupId: string) => client.deleteOptimizationGroup(groupId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["optimization-groups"] });
    },
  });
}
