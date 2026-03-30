import { useQuery } from "@tanstack/react-query";
import { getClient } from "@/lib/services";

export function useThresholdProfiles() {
  const client = getClient();
  return useQuery({
    queryKey: ["threshold-profiles"],
    queryFn: () => client.getThresholdProfiles(),
  });
}
