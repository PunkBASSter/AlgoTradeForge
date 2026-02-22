"use client";

import {
  createContext,
  useCallback,
  useContext,
  useState,
  useEffect,
  type ReactNode,
} from "react";

type ToastVariant = "success" | "error" | "info";

interface ToastEntry {
  id: number;
  message: string;
  variant: ToastVariant;
}

interface ToastContextValue {
  toast: (message: string, variant?: ToastVariant) => void;
}

const ToastContext = createContext<ToastContextValue | null>(null);

const variantClasses: Record<ToastVariant, string> = {
  success: "border-accent-green text-accent-green",
  error: "border-accent-red text-accent-red",
  info: "border-accent-blue text-accent-blue",
};

let nextId = 0;

function ToastItem({
  entry,
  onDismiss,
}: {
  entry: ToastEntry;
  onDismiss: (id: number) => void;
}) {
  useEffect(() => {
    const timer = setTimeout(() => {
      onDismiss(entry.id);
    }, 5000);
    return () => clearTimeout(timer);
  }, [entry.id, onDismiss]);

  return (
    <div
      className={`pointer-events-auto flex items-center gap-3 rounded-md border bg-bg-surface px-4 py-3 text-sm shadow-lg ${variantClasses[entry.variant]}`}
      role="alert"
    >
      <span className="flex-1 text-text-primary">{entry.message}</span>
      <button
        type="button"
        onClick={() => onDismiss(entry.id)}
        className="text-text-muted transition-colors hover:text-text-primary"
        aria-label="Dismiss"
      >
        &times;
      </button>
    </div>
  );
}

export function ToastProvider({ children }: { children: ReactNode }) {
  const [toasts, setToasts] = useState<ToastEntry[]>([]);

  const dismiss = useCallback((id: number) => {
    setToasts((prev) => prev.filter((t) => t.id !== id));
  }, []);

  const toast = useCallback((message: string, variant: ToastVariant = "info") => {
    const id = nextId++;
    setToasts((prev) => [...prev, { id, message, variant }]);
  }, []);

  return (
    <ToastContext.Provider value={{ toast }}>
      {children}
      <div className="pointer-events-none fixed bottom-4 right-4 z-50 flex flex-col gap-2">
        {toasts.map((entry) => (
          <ToastItem key={entry.id} entry={entry} onDismiss={dismiss} />
        ))}
      </div>
    </ToastContext.Provider>
  );
}

export function useToast(): ToastContextValue {
  const ctx = useContext(ToastContext);
  if (!ctx) {
    throw new Error("useToast must be used within a ToastProvider");
  }
  return ctx;
}
