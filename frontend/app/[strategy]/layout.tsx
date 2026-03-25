"use client";

import { use, useState, useEffect, useCallback } from "react";
import { usePathname } from "next/navigation";
import { useQueryClient } from "@tanstack/react-query";
import { StrategySelector } from "@/components/features/dashboard/strategy-selector";
import { RunNewPanel } from "@/components/features/dashboard/run-new-panel";
import { useRunNew } from "@/contexts/run-new-context";

const SIDEBAR_KEY = "sidebar-collapsed";

function useSidebarCollapsed() {
  const [collapsed, setCollapsed] = useState(false);

  useEffect(() => {
    setCollapsed(localStorage.getItem(SIDEBAR_KEY) === "true");
  }, []);

  const toggle = useCallback(() => {
    setCollapsed((prev) => {
      const next = !prev;
      localStorage.setItem(SIDEBAR_KEY, String(next));
      return next;
    });
  }, []);

  return { collapsed, toggle };
}

export default function StrategyLayout({
  children,
  params,
}: {
  children: React.ReactNode;
  params: Promise<{ strategy: string }>;
}) {
  const { strategy } = use(params);
  const selected = strategy === "all" ? null : strategy;
  const { collapsed, toggle } = useSidebarCollapsed();
  const { open, setOpen, initialContent } = useRunNew();
  const pathname = usePathname();
  const queryClient = useQueryClient();

  const mode = pathname.endsWith("/optimization")
    ? "optimization"
    : pathname.endsWith("/live")
      ? "live"
      : "backtest";

  const handleRunNewSuccess = () => {
    queryClient.invalidateQueries({ queryKey: ["backtests"] });
    queryClient.invalidateQueries({ queryKey: ["optimizations"] });
    queryClient.invalidateQueries({ queryKey: ["live-sessions"] });
  };

  return (
    <div className="flex min-h-[calc(100vh-100px)]">
      {/* Collapsible sidebar */}
      <aside
        className={`hidden xl:flex shrink-0 border-r border-border-default bg-bg-surface transition-[width] duration-200 ${
          collapsed ? "w-3" : "w-56"
        }`}
      >
        {collapsed ? (
          <button
            onClick={toggle}
            className="w-full flex items-start justify-center pt-4 text-text-muted hover:text-text-primary transition-colors"
            aria-label="Expand sidebar"
          >
            <svg
              xmlns="http://www.w3.org/2000/svg"
              viewBox="0 0 20 20"
              fill="currentColor"
              className="w-3.5 h-3.5"
            >
              <path
                fillRule="evenodd"
                d="M8.22 5.22a.75.75 0 0 1 1.06 0l4.25 4.25a.75.75 0 0 1 0 1.06l-4.25 4.25a.75.75 0 0 1-1.06-1.06L11.94 10 8.22 6.28a.75.75 0 0 1 0-1.06Z"
                clipRule="evenodd"
              />
            </svg>
          </button>
        ) : (
          <div className="flex-1 p-4 overflow-y-auto">
            <div className="flex items-center justify-between mb-2">
              <h3 className="text-xs font-semibold text-text-muted uppercase tracking-wider px-3">
                Strategies
              </h3>
              <button
                onClick={toggle}
                className="text-text-muted hover:text-text-primary transition-colors p-1"
                aria-label="Collapse sidebar"
              >
                <svg
                  xmlns="http://www.w3.org/2000/svg"
                  viewBox="0 0 20 20"
                  fill="currentColor"
                  className="w-3.5 h-3.5"
                >
                  <path
                    fillRule="evenodd"
                    d="M11.78 5.22a.75.75 0 0 1 0 1.06L8.06 10l3.72 3.72a.75.75 0 1 1-1.06 1.06l-4.25-4.25a.75.75 0 0 1 0-1.06l4.25-4.25a.75.75 0 0 1 1.06 0Z"
                    clipRule="evenodd"
                  />
                </svg>
              </button>
            </div>
            <StrategySelector selected={selected} />
          </div>
        )}
      </aside>
      <div className="flex-1 p-6 space-y-4 overflow-hidden">{children}</div>

      <RunNewPanel
        open={open}
        onClose={() => setOpen(false)}
        mode={mode}
        selectedStrategy={selected}
        onSuccess={handleRunNewSuccess}
        initialContent={initialContent}
      />
    </div>
  );
}
