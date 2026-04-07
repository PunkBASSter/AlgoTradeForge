"use client";

import { useEffect } from "react";
import { useRouter } from "next/navigation";
import { useStrategies } from "@/hooks/use-strategies";
import { SESSION_KEYS } from "@/lib/constants";

export default function Home() {
  const router = useRouter();
  const { data: strategies, isLoading } = useStrategies();

  useEffect(() => {
    // Fast path: restore last route without waiting for API
    const lastRoute = sessionStorage.getItem(SESSION_KEYS.LAST_ROUTE);
    if (lastRoute) {
      router.replace(lastRoute);
      return;
    }

    if (isLoading) return;

    if (strategies && strategies.length > 0) {
      router.replace(`/${strategies[0]}/backtest`);
    } else {
      router.replace("/all/backtest");
    }
  }, [strategies, isLoading, router]);

  return (
    <div className="flex items-center justify-center min-h-[50vh]">
      <p className="text-sm text-text-muted">Loading...</p>
    </div>
  );
}
