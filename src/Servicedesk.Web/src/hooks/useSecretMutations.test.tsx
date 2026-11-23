import * as React from "react";
import { describe, expect, it, vi, beforeEach } from "vitest";
import { act, renderHook, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { ApiError } from "@/lib/http";
import { useSecretMutations } from "./useSecretMutations";

const toastMock = vi.hoisted(() => ({ success: vi.fn(), error: vi.fn() }));
vi.mock("sonner", () => ({ toast: toastMock }));

function setup(overrides: Partial<Parameters<typeof useSecretMutations>[0]> = {}) {
  const qc = new QueryClient({ defaultOptions: { mutations: { retry: false } } });
  const invalidate = vi.spyOn(qc, "invalidateQueries");
  const wrapper = ({ children }: { children: React.ReactNode }) => (
    <QueryClientProvider client={qc}>{children}</QueryClientProvider>
  );
  const onSaved = vi.fn();
  const { result } = renderHook(
    () =>
      useSecretMutations({
        label: "API key",
        save: () => Promise.resolve(),
        clear: () => Promise.resolve(),
        invalidate: [["secret"], ["status"]],
        onSaved,
        ...overrides,
      }),
    { wrapper },
  );
  return { result, invalidate, onSaved };
}

describe("useSecretMutations", () => {
  beforeEach(() => {
    toastMock.success.mockReset();
    toastMock.error.mockReset();
  });

  it("saves, empties the draft and refreshes every listed query", async () => {
    const { result, invalidate, onSaved } = setup();
    act(() => result.current.save.mutate());
    await waitFor(() => expect(result.current.save.isSuccess).toBe(true));
    expect(toastMock.success).toHaveBeenCalledWith("API key saved");
    expect(onSaved).toHaveBeenCalledOnce();
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ["secret"] });
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ["status"] });
  });

  it("never fails silently on clear and prefers the server message", async () => {
    const { result } = setup({
      clear: () => Promise.reject(new ApiError(409, "/x", "conflict", { message: "Integration is still enabled" })),
    });
    act(() => result.current.clear.mutate());
    await waitFor(() => expect(result.current.clear.isError).toBe(true));
    expect(toastMock.error).toHaveBeenCalledWith("Integration is still enabled");
  });

  it("falls back to a readable text when the server gives none", async () => {
    const { result } = setup({ save: () => Promise.reject(new ApiError(500, "/x", "boom")) });
    act(() => result.current.save.mutate());
    await waitFor(() => expect(result.current.save.isError).toBe(true));
    expect(toastMock.error).toHaveBeenCalledWith("API key could not be saved");
  });
});
