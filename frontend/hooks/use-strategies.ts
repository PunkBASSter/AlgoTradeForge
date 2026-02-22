// T050 - TanStack Query hook for strategies list

import { useQuery } from "@tanstack/react-query";
import { getClient } from "@/lib/services";

export function useStrategies() {
  const client = getClient();
  return useQuery({
    queryKey: ["strategies"],
    queryFn: () => client.getStrategies(),
  });
}
