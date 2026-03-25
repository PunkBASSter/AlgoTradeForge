"use client";

interface ToggleSwitchProps {
  leftLabel: string;
  rightLabel: string;
  checked: boolean;
  onChange: (checked: boolean) => void;
  disabled?: boolean;
}

export function ToggleSwitch({
  leftLabel,
  rightLabel,
  checked,
  onChange,
  disabled = false,
}: ToggleSwitchProps) {
  return (
    <div className="flex items-center gap-3">
      <span
        className={`text-sm font-medium ${!checked ? "text-text-primary" : "text-text-secondary"}`}
      >
        {leftLabel}
      </span>
      <button
        type="button"
        role="switch"
        aria-checked={checked}
        aria-label={`${leftLabel} or ${rightLabel}`}
        disabled={disabled}
        onClick={() => onChange(!checked)}
        className={`relative inline-flex h-6 w-11 shrink-0 cursor-pointer rounded-full border-2 border-transparent transition-colors focus:outline-none focus:ring-2 focus:ring-accent-blue focus:ring-offset-1 focus:ring-offset-bg-base disabled:cursor-not-allowed disabled:opacity-50 ${
          checked ? "bg-accent-blue" : "bg-bg-surface"
        }`}
      >
        <span
          className={`pointer-events-none inline-block h-5 w-5 rounded-full bg-white shadow-sm transition-transform ${
            checked ? "translate-x-5" : "translate-x-0"
          }`}
        />
      </button>
      <span
        className={`text-sm font-medium ${checked ? "text-text-primary" : "text-text-secondary"}`}
      >
        {rightLabel}
      </span>
    </div>
  );
}
