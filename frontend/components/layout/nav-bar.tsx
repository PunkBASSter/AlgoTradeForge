"use client";

import Link from "next/link";
import { usePathname } from "next/navigation";
import { useRunNew } from "@/contexts/run-new-context";
import { Button } from "@/components/ui/button";

const modeTabs = [
  { id: "backtest", label: "Backtest" },
  { id: "optimization", label: "Optimization" },
  { id: "live", label: "Live Trading" },
] as const;

function parseRoute(pathname: string): {
  strategy: string | null;
  mode: string | null;
} {
  // Pattern: /{strategy}/{mode}
  const segments = pathname.split("/").filter(Boolean);
  if (segments.length >= 2) {
    return { strategy: segments[0], mode: segments[1] };
  }
  return { strategy: null, mode: null };
}

export function NavBar() {
  const pathname = usePathname();
  const { setOpen } = useRunNew();
  const { strategy, mode } = parseRoute(pathname);

  // Only show mode tabs when we're on a strategy page
  const showTabs = strategy !== null && mode !== null;

  return (
    <header className="flex items-center justify-between px-6 py-3 border-b border-border-default bg-bg-surface">
      <div className="flex items-center gap-8">
        <Link
          href="/"
          className="text-lg font-bold text-text-primary tracking-tight"
        >
          AlgoTradeForge
        </Link>
        {showTabs && (
          <nav className="flex items-center gap-1">
            {modeTabs.map((tab) => {
              const isActive = mode === tab.id;
              return (
                <Link
                  key={tab.id}
                  href={`/${strategy}/${tab.id}`}
                  className={`px-3 py-1.5 text-sm rounded transition-colors ${
                    isActive
                      ? "bg-accent-blue text-white"
                      : "text-text-secondary hover:bg-bg-hover hover:text-text-primary"
                  }`}
                >
                  {tab.label}
                </Link>
              );
            })}
          </nav>
        )}
      </div>
      {showTabs && (
        <Button variant="primary" onClick={() => setOpen(true)}>
          + Run New
        </Button>
      )}
    </header>
  );
}
