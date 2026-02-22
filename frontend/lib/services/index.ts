// T008 - Service layer barrel export

import { apiClient } from "./api-client";
import { mockClient } from "./mock-client";

export function getClient() {
  if (process.env.NEXT_PUBLIC_MOCK_MODE === "true") {
    return mockClient;
  }
  return apiClient;
}

export { apiClient, mockClient };
