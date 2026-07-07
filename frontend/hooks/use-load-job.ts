import { useEffect } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { DataApiError, dataApi } from "@/lib/services/data-api";
import type { LoadJobSnapshotWire } from "@/types/data-tab";

const isTerminal = (s: string | undefined) => s === "complete" || s === "error";

export function useLoadJob(jobId: string | null) {
  const queryClient = useQueryClient();
  const query = useQuery<LoadJobSnapshotWire>({
    queryKey: ["data", "load-job", jobId],
    queryFn: ({ signal }) => dataApi.getLoadJob(jobId!, signal),
    enabled: !!jobId,
    retry: false,
    refetchInterval: (q) => {
      if (isTerminal(q.state.data?.state)) return false;
      if (q.state.error instanceof DataApiError && q.state.error.status === 404) return false;
      return 2_000;
    },
  });

  const terminalState = isTerminal(query.data?.state) ? query.data?.state : undefined;
  useEffect(() => {
    if (!terminalState) return;
    // Materialized months change the catalog + coverage; refresh both once per completion.
    void queryClient.invalidateQueries({ queryKey: ["data", "assets"] });
    void queryClient.invalidateQueries({ queryKey: ["data", "exchanges"] });
    void queryClient.invalidateQueries({ queryKey: ["data", "coverage"] });
  }, [terminalState, queryClient]);

  return query;
}
