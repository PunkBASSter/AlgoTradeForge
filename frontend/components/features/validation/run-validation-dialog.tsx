"use client";

import { useState, useEffect, useRef } from "react";
import { Button } from "@/components/ui/button";
import { useThresholdProfiles } from "@/hooks/use-threshold-profiles";

const FALLBACK_PROFILES: { value: string; label: string }[] = [
  { value: "Crypto-Standard", label: "Crypto-Standard" },
  { value: "Crypto-Conservative", label: "Crypto-Conservative" },
];

interface RunValidationDialogProps {
  open: boolean;
  onClose: () => void;
  onSubmit: (profileName: string) => void;
  loading?: boolean;
}

export function RunValidationDialog({
  open,
  onClose,
  onSubmit,
  loading = false,
}: RunValidationDialogProps) {
  const [selectedProfile, setSelectedProfile] = useState("Crypto-Standard");
  const panelRef = useRef<HTMLDivElement>(null);
  const previousFocusRef = useRef<HTMLElement | null>(null);

  // Fetch profiles from API (falls back to hardcoded if API unavailable)
  const { data: profiles } = useThresholdProfiles();
  const profileOptions = profiles && profiles.length > 0
    ? profiles.map((p) => ({ value: p.name, label: p.name }))
    : FALLBACK_PROFILES;

  // Reset selection when dialog opens
  useEffect(() => {
    if (open) setSelectedProfile("Crypto-Standard");
  }, [open]);

  useEffect(() => {
    if (!open) return;

    previousFocusRef.current = document.activeElement as HTMLElement | null;

    function handleKeyDown(e: KeyboardEvent) {
      if (e.key === "Escape" && !loading) {
        onClose();
      }
    }

    document.addEventListener("keydown", handleKeyDown);

    requestAnimationFrame(() => {
      panelRef.current?.querySelector<HTMLElement>("select")?.focus();
    });

    return () => {
      document.removeEventListener("keydown", handleKeyDown);
      previousFocusRef.current?.focus();
    };
  }, [open, onClose, loading]);

  if (!open) return null;

  return (
    <div className="fixed inset-0 z-40 flex items-center justify-center">
      {/* Backdrop */}
      <div
        className="fixed inset-0 bg-black/50 transition-opacity"
        onClick={loading ? undefined : onClose}
        aria-hidden="true"
      />

      {/* Dialog */}
      <div
        ref={panelRef}
        role="dialog"
        aria-modal="true"
        aria-label="Run Validation"
        className="relative z-50 w-full max-w-sm rounded-lg border border-border-default bg-bg-surface shadow-xl"
      >
        {/* Header */}
        <div className="flex items-center justify-between border-b border-border-default px-5 py-4">
          <h2 className="text-base font-semibold text-text-primary">
            Run Validation
          </h2>
          <button
            type="button"
            onClick={onClose}
            disabled={loading}
            className="rounded-md p-1 text-text-muted transition-colors hover:bg-bg-hover hover:text-text-primary disabled:opacity-50"
            aria-label="Close dialog"
          >
            <svg
              xmlns="http://www.w3.org/2000/svg"
              className="h-5 w-5"
              viewBox="0 0 20 20"
              fill="currentColor"
              aria-hidden="true"
            >
              <path
                fillRule="evenodd"
                d="M4.293 4.293a1 1 0 011.414 0L10 8.586l4.293-4.293a1 1 0 111.414 1.414L11.414 10l4.293 4.293a1 1 0 01-1.414 1.414L10 11.414l-4.293 4.293a1 1 0 01-1.414-1.414L8.586 10 4.293 5.707a1 1 0 010-1.414z"
                clipRule="evenodd"
              />
            </svg>
          </button>
        </div>

        {/* Body */}
        <div className="px-5 py-4 space-y-4">
          <div className="space-y-1.5">
            <label
              htmlFor="threshold-profile"
              className="block text-sm font-medium text-text-secondary"
            >
              Threshold Profile
            </label>
            <select
              id="threshold-profile"
              value={selectedProfile}
              onChange={(e) => setSelectedProfile(e.target.value)}
              disabled={loading}
              className="w-full rounded-md border border-border-default bg-bg-base px-3 py-2 text-sm text-text-primary focus:border-accent-blue focus:outline-none focus:ring-1 focus:ring-accent-blue disabled:opacity-50"
            >
              {profileOptions.map((p) => (
                <option key={p.value} value={p.value}>
                  {p.label}
                </option>
              ))}
            </select>
          </div>

          <p className="text-xs text-text-muted">
            {selectedProfile.includes("Conservative")
              ? "Tighter thresholds for shorter data and higher noise"
              : selectedProfile === "Crypto-Standard"
                ? "Balanced thresholds for strategies with adequate data"
                : "Custom threshold profile"}
          </p>
        </div>

        {/* Footer */}
        <div className="flex items-center justify-end gap-2 border-t border-border-default px-5 py-3">
          <Button
            variant="secondary"
            onClick={onClose}
            disabled={loading}
          >
            Cancel
          </Button>
          <Button
            variant="primary"
            loading={loading}
            onClick={() => onSubmit(selectedProfile)}
          >
            Start Validation
          </Button>
        </div>
      </div>
    </div>
  );
}
