import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { GroupCard } from "../group-card";
import type { CollectionGroupSummary } from "@/types/data-tab";

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
