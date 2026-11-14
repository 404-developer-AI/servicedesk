import { describe, expect, it } from "vitest";
import { act, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ConfirmProvider, useConfirm } from "@/components/ConfirmDialog";

function Harness({ onResult }: { onResult: (ok: boolean) => void }) {
  const confirm = useConfirm();
  return (
    <button
      type="button"
      onClick={async () =>
        onResult(await confirm({ title: "Delete queue", description: 'Delete "Support"?', tone: "destructive" }))
      }
    >
      open
    </button>
  );
}

describe("ConfirmDialog", () => {
  it("resolves true on confirm and false on cancel", async () => {
    const results: boolean[] = [];
    const user = userEvent.setup();
    render(
      <ConfirmProvider>
        <Harness onResult={(ok) => results.push(ok)} />
      </ConfirmProvider>,
    );

    await user.click(screen.getByText("open"));
    expect(screen.getByText("Delete queue")).toBeInTheDocument();
    expect(screen.getByText('Delete "Support"?')).toBeInTheDocument();
    await act(() => user.click(screen.getByRole("button", { name: "Delete" })));

    await user.click(screen.getByText("open"));
    await act(() => user.click(screen.getByRole("button", { name: "Cancel" })));

    expect(results).toEqual([true, false]);
  });

  it("resolves false when closed with Escape", async () => {
    const results: boolean[] = [];
    const user = userEvent.setup();
    render(
      <ConfirmProvider>
        <Harness onResult={(ok) => results.push(ok)} />
      </ConfirmProvider>,
    );
    await user.click(screen.getByText("open"));
    await act(() => user.keyboard("{Escape}"));
    expect(results).toEqual([false]);
  });
});
