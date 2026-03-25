"use client";

import { useEffect } from "react";
import { useRouter } from "next/navigation";
import { useStrategies } from "@/hooks/use-strategies";

export default function Home() {
  const router = useRouter();
  const { data: strategies, isLoading } = useStrategies();

  useEffect(() => {
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
