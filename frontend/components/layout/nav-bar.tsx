"use client";

import { useEffect } from "react";
import Link from "next/link";
import { usePathname } from "next/navigation";
import { useRunNew } from "@/contexts/run-new-context";
import { Button } from "@/components/ui/button";
import { SESSION_KEYS } from "@/lib/constants";

const modeTabs = [
  { id: "backtest", label: "Backtest" },
  { id: "optimization", label: "Optimization" },
  { id: "validation", label: "Validation" },
  { id: "live", label: "Live Trading" },
] as const;

// Top-level route prefixes that are NOT strategy names
const reservedPrefixes = new Set(["report", "debug", "dashboard"]);

function parseRoute(pathname: string): {
  strategy: string | null;
  mode: string | null;
} {
  // Pattern: /{strategy}/{mode} — exclude reserved prefixes
  const segments = pathname.split("/").filter(Boolean);
  if (segments.length >= 2 && !reservedPrefixes.has(segments[0])) {
    return { strategy: segments[0], mode: segments[1] };
  }
  return { strategy: null, mode: null };
}

export function NavBar() {
  const pathname = usePathname();
  const { setOpen } = useRunNew();
  const { strategy, mode } = parseRoute(pathname);

  // Persist last strategy route so homepage can restore it
  useEffect(() => {
    if (strategy && mode && modeTabs.some((t) => t.id === mode)) {
      sessionStorage.setItem(SESSION_KEYS.LAST_ROUTE, `/${strategy}/${mode}`);
    }
  }, [strategy, mode]);

  // Only show mode tabs when we're on a strategy page
  const showTabs = strategy !== null && mode !== null;

  return (
    <header className="flex items-center justify-between px-6 py-3 border-b border-border-default bg-bg-surface">
      <div className="flex items-center gap-8">
        <Link
          href={showTabs ? `/${strategy}/${mode}` : "/"}
          className="text-lg font-bold text-text-primary tracking-tight"
        >
          AlgoTradeForge
        </Link>
        {showTabs && (
          <nav className="flex items-center gap-1" role="tablist">
            {modeTabs.map((tab) => {
              const isActive = mode === tab.id;
              return (
                <Link
                  key={tab.id}
                  href={`/${strategy}/${tab.id}`}
                  role="tab"
                  aria-selected={isActive}
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
