import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { getClient } from "@/lib/services";
import type { RunValidationRequest, ValidationListParams } from "@/types/validation";

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
