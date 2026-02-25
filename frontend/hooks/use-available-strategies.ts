import { useQuery } from "@tanstack/react-query";
import { getClient } from "@/lib/services";
import type { StrategyDescriptor } from "@/types/api";

export function useAvailableStrategies() {
  const client = getClient();
  return useQuery<StrategyDescriptor[]>({
    queryKey: ["strategies", "available"],
    queryFn: () => client.getAvailableStrategies(),
  });
}
