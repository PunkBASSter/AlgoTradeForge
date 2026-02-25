import { useQuery } from "@tanstack/react-query";
import { getClient } from "@/lib/services";

export function useAvailableStrategies() {
  const client = getClient();
  return useQuery({
    queryKey: ["strategies", "available"],
    queryFn: () => client.getAvailableStrategies(),
  });
}
