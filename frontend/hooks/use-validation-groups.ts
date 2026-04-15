// T055 - TanStack Query hooks for validation groups

import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { getClient } from "@/lib/services";
import type { RunGroupValidationRequest } from "@/types/validation-group";

export function useRunGroupValidation() {
  const client = getClient();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (req: RunGroupValidationRequest) => client.runGroupValidation(req),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["validation-groups"] });
    },
  });
}

export function useValidationGroupDetail(groupId: string) {
  const client = getClient();
  return useQuery({
    queryKey: ["validation-group", groupId],
    queryFn: () => client.getValidationGroup(groupId),
    enabled: !!groupId,
  });
}

export function useValidationGroupStatus(groupId: string) {
  const client = getClient();
  return useQuery({
    queryKey: ["validation-group-status", groupId],
    queryFn: () => client.getValidationGroupStatus(groupId),
    enabled: !!groupId,
    refetchInterval: (query) => {
      const status = query.state.data?.status;
      if (status === "Completed" || status === "Failed" || status === "Cancelled")
        return false;
      return 2000;
    },
  });
}
