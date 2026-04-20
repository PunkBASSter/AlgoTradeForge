import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { getClient } from "@/lib/services";
import type { RunValidationRequest, ValidationListParams } from "@/types/validation";
import type { RunGroupValidationRequest } from "@/types/validation-group";
import type { ValidationGroupListParams } from "@/lib/services/api-client";

// ---------------------------------------------------------------------------
// Single validation run hooks
// ---------------------------------------------------------------------------

export function useValidationList(params: ValidationListParams) {
  const client = getClient();
  return useQuery({
    queryKey: ["validations", params],
    queryFn: () => client.getValidations(params),
  });
}

export function useValidationDetail(id: string) {
  const client = getClient();
  return useQuery({
    queryKey: ["validation", id],
    queryFn: () => client.getValidation(id),
    enabled: !!id,
  });
}

export function useValidationEquity(id: string, enabled = true) {
  const client = getClient();
  return useQuery({
    queryKey: ["validation-equity", id],
    queryFn: () => client.getValidationEquity(id),
    enabled: !!id && enabled,
  });
}

export function useValidationStatus(id: string) {
  const client = getClient();
  return useQuery({
    queryKey: ["validation-status", id],
    queryFn: () => client.getValidationStatus(id),
    enabled: !!id,
    refetchInterval: (query) => {
      const status = query.state.data?.status;
      if (status === "Completed" || status === "Failed" || status === "Cancelled")
        return false;
      return 2000;
    },
  });
}

export function useRunValidation() {
  const client = getClient();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (req: RunValidationRequest) => client.runValidation(req),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["validations"] });
    },
  });
}

export function useCancelValidation() {
  const client = getClient();
  return useMutation({
    mutationFn: (id: string) => client.cancelValidation(id),
  });
}

export function useDeleteValidation() {
  const client = getClient();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => client.deleteValidation(id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["validations"] });
    },
  });
}

// ---------------------------------------------------------------------------
// T068 - Validation group hooks
// ---------------------------------------------------------------------------

export function useValidationGroups(params: ValidationGroupListParams) {
  const client = getClient();
  return useQuery({
    queryKey: ["validation-groups", params],
    queryFn: () => client.getValidationGroups(params),
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

export function useValidationGroupStatus(groupId: string, enabled = true) {
  const client = getClient();
  return useQuery({
    queryKey: ["validation-group-status", groupId],
    queryFn: () => client.getValidationGroupStatus(groupId),
    enabled: !!groupId && enabled,
    refetchInterval: (query) => {
      const status = query.state.data?.status;
      if (status === "Completed" || status === "Failed" || status === "Cancelled")
        return false;
      return 2000;
    },
  });
}

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

export function useCancelValidationGroup() {
  const client = getClient();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (groupId: string) => client.cancelValidationGroup(groupId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["task-queue"] });
      queryClient.invalidateQueries({ queryKey: ["validations"] });
      queryClient.invalidateQueries({ queryKey: ["validation-groups"] });
      queryClient.invalidateQueries({ queryKey: ["validation-group"] });
    },
  });
}

export function useDeleteValidationGroup() {
  const client = getClient();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (groupId: string) => client.deleteValidationGroup(groupId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["validation-groups"] });
    },
  });
}
