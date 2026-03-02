"use client";

import { use } from "react";
import { StrategySelector } from "@/components/features/dashboard/strategy-selector";

export default function StrategyLayout({
  children,
  params,
}: {
  children: React.ReactNode;
  params: Promise<{ strategy: string }>;
}) {
  const { strategy } = use(params);
  const selected = strategy === "all" ? null : strategy;

  return (
    <div className="flex min-h-[calc(100vh-100px)]">
      <aside className="hidden xl:block w-56 shrink-0 border-r border-border-default bg-bg-surface p-4 overflow-y-auto">
        <StrategySelector selected={selected} />
      </aside>
      <div className="flex-1 p-6 space-y-4 overflow-hidden">{children}</div>
    </div>
  );
}
