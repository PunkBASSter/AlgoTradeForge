import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { getClient } from "@/lib/services";

export function useLiveSessions(enabled = true) {
  const client = getClient();
  return useQuery({
    queryKey: ["live-sessions"],
    queryFn: () => client.getLiveSessions(),
    enabled,
    refetchInterval: 10_000,
  });
}

export function useLiveSessionDetail(id: string) {
  const client = getClient();
  return useQuery({
    queryKey: ["live-session", id],
    queryFn: () => client.getLiveSession(id),
    enabled: !!id,
    refetchInterval: 5_000,
  });
}

export function useStopLiveSession() {
  const client = getClient();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => client.stopLiveSession(id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["live-sessions"] });
    },
  });
}
