"use client";

import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { getClient } from "@/lib/services";

const TASK_QUEUE_KEY = ["task-queue"] as const;

export function useTaskQueue() {
  const client = getClient();
  return useQuery({
    queryKey: TASK_QUEUE_KEY,
    queryFn: () => client.getTaskQueue(),
    refetchInterval: (query) => {
      const data = query.state.data;
      if (!data || data.activeTasks.length === 0) return false;
      return 2000;
    },
  });
}

export function useCancelTask() {
  const client = getClient();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (taskId: string) => client.cancelTask(taskId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: TASK_QUEUE_KEY });
      queryClient.invalidateQueries({ queryKey: ["optimizations"] });
      queryClient.invalidateQueries({ queryKey: ["optimization-groups"] });
      queryClient.invalidateQueries({ queryKey: ["optimization-group"] });
      queryClient.invalidateQueries({ queryKey: ["validations"] });
      queryClient.invalidateQueries({ queryKey: ["validation-groups"] });
      queryClient.invalidateQueries({ queryKey: ["validation-group"] });
    },
  });
}

export function usePurgeQueue() {
  const client = getClient();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: () => client.purgeQueue(),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: TASK_QUEUE_KEY });
      queryClient.invalidateQueries({ queryKey: ["optimizations"] });
      queryClient.invalidateQueries({ queryKey: ["optimization-groups"] });
      queryClient.invalidateQueries({ queryKey: ["optimization-group"] });
      queryClient.invalidateQueries({ queryKey: ["validations"] });
      queryClient.invalidateQueries({ queryKey: ["validation-groups"] });
      queryClient.invalidateQueries({ queryKey: ["validation-group"] });
    },
  });
}
