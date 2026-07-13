import { useQuery } from "@tanstack/react-query";
import { dataApi } from "@/lib/services/data-api";
import type { JobEnvelope } from "@/types/data-tab";

export function useJobs() {
  return useQuery<JobEnvelope[]>({
    queryKey: ["data", "jobs"],
    queryFn: ({ signal }) => dataApi.getJobs(undefined, signal),
    refetchInterval: 5000,
  });
}
