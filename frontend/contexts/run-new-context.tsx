"use client";

import { createContext, useContext, useState, useCallback, type ReactNode } from "react";

interface RunNewContextValue {
  open: boolean;
  setOpen: (open: boolean) => void;
  initialContent: Record<string, unknown> | null;
  openWithContent: (content: Record<string, unknown>) => void;
}

const RunNewContext = createContext<RunNewContextValue | null>(null);

export function RunNewProvider({ children }: { children: ReactNode }) {
  const [open, setOpen] = useState(false);
  const [initialContent, setInitialContent] = useState<Record<string, unknown> | null>(null);

  const handleSetOpen = useCallback((value: boolean) => {
    setOpen(value);
    if (!value) setInitialContent(null);
  }, []);

  const openWithContent = useCallback((content: Record<string, unknown>) => {
    setInitialContent(content);
    setOpen(true);
  }, []);

  return (
    <RunNewContext.Provider value={{ open, setOpen: handleSetOpen, initialContent, openWithContent }}>
      {children}
    </RunNewContext.Provider>
  );
}

export function useRunNew(): RunNewContextValue {
  const ctx = useContext(RunNewContext);
  if (!ctx) throw new Error("useRunNew must be used within RunNewProvider");
  return ctx;
}
