import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { GroupCard } from "../group-card";
import type { CollectionGroupSummary, DesiredStateReport, TupleStatusValue } from "@/types/data-tab";

const toastSpy = vi.fn();

vi.mock("@/components/ui/toast", () => ({
  useToast: () => ({ toast: toastSpy }),
}));

const SUMMARY: CollectionGroupSummary = {
  name: "my-group",
  enabled: true,
  exchanges: ["binance"],
  symbol_count: 2,
  feed_count: 1,
  etag: 'W/"x"',
};

function makeTupleDesiredState(status: TupleStatusValue, extra: TupleStatusValue[] = []): DesiredStateReport {
  const makeTuple = (s: TupleStatusValue) => ({
    exchange: "binance",
    canonical: "BTC/USDT-PERP",
    dir: "BTCUSDT_perp",
    feed_name: "candles",
    interval: "1h",
    status: s,
    months_expected: 12,
    months_covered: 12,
    collect: "eager",
    history_start: "2025-01",
    is_derived: false,
    groups: ["my-group"],
  });
  return {
    computed_at: "2026-07-11T00:00:00Z",
    tuples: [makeTuple(status), ...extra.map(makeTuple)],
    orphaned: [],
    orphaned_total: 0,
    conflicts: [],
  };
}

describe("GroupCard convergence chips — blocked + awaiting-data", () => {
  it("blocked chip renders with error styling (text-accent-red)", () => {
    render(
      <GroupCard
        summary={SUMMARY}
        desiredState={makeTupleDesiredState("blocked")}
        onEdit={() => {}}
        onDelete={vi.fn()}
      />,
    );
    const chip = screen.getByText(/1 blocked/i);
    expect(chip).toHaveClass("text-accent-red");
  });

  it("awaiting-data chip renders with warning styling (text-accent-yellow)", () => {
    render(
      <GroupCard
        summary={SUMMARY}
        desiredState={makeTupleDesiredState("awaiting-data")}
        onEdit={() => {}}
        onDelete={vi.fn()}
      />,
    );
    const chip = screen.getByText(/1 awaiting.data/i);
    expect(chip).toHaveClass("text-accent-yellow");
  });

  it("blocked is not counted in missing", () => {
    render(
      <GroupCard
        summary={SUMMARY}
        desiredState={makeTupleDesiredState("missing", ["blocked"])}
        onEdit={() => {}}
        onDelete={vi.fn()}
      />,
    );
    expect(screen.getByText(/1 missing/i)).toBeDefined();
    expect(screen.queryByText(/2 missing/i)).toBeNull();
    expect(screen.getByText(/1 blocked/i)).toBeDefined();
  });

  it("awaiting-data is not counted in missing", () => {
    render(
      <GroupCard
        summary={SUMMARY}
        desiredState={makeTupleDesiredState("missing", ["awaiting-data"])}
        onEdit={() => {}}
        onDelete={vi.fn()}
      />,
    );
    expect(screen.getByText(/1 missing/i)).toBeDefined();
    expect(screen.queryByText(/2 missing/i)).toBeNull();
    expect(screen.getByText(/1 awaiting.data/i)).toBeDefined();
  });
});

describe("GroupCard delete", () => {
  beforeEach(() => {
    toastSpy.mockClear();
  });

  it("confirm-declined does NOT call onDelete", () => {
    const onDelete = vi.fn().mockResolvedValue(undefined);
    vi.spyOn(window, "confirm").mockReturnValueOnce(false);

    render(
      <GroupCard
        summary={SUMMARY}
        desiredState={undefined}
        onEdit={() => {}}
        onDelete={onDelete}
      />,
    );
    fireEvent.click(screen.getByRole("button", { name: /delete/i }));

    expect(onDelete).not.toHaveBeenCalled();
  });

  it("confirm-accepted calls onDelete", async () => {
    const onDelete = vi.fn().mockResolvedValue(undefined);
    vi.spyOn(window, "confirm").mockReturnValueOnce(true);

    render(
      <GroupCard
        summary={SUMMARY}
        desiredState={undefined}
        onEdit={() => {}}
        onDelete={onDelete}
      />,
    );
    fireEvent.click(screen.getByRole("button", { name: /delete/i }));

    await waitFor(() => expect(onDelete).toHaveBeenCalledOnce());
  });

  it("deleteGroup rejects → error toast fired", async () => {
    const onDelete = vi.fn().mockRejectedValue(new Error("server error"));
    vi.spyOn(window, "confirm").mockReturnValueOnce(true);

    render(
      <GroupCard
        summary={SUMMARY}
        desiredState={undefined}
        onEdit={() => {}}
        onDelete={onDelete}
      />,
    );
    fireEvent.click(screen.getByRole("button", { name: /delete/i }));

    await waitFor(() =>
      expect(toastSpy).toHaveBeenCalledWith(
        expect.stringContaining("server error"),
        "error",
      ),
    );
  });
});
