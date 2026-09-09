import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { debounceAsync } from "@/lib/asyncDebounce";

describe("debounceAsync", () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });
  afterEach(() => {
    vi.useRealTimers();
  });

  it("collapses a burst of calls into one invocation with the latest argument", async () => {
    const fn = vi.fn(async (q: string) => `result:${q}`);
    const lookup = debounceAsync(fn, 250);

    const p1 = lookup("t");
    const p2 = lookup("te");
    const p3 = lookup("tem");
    await vi.advanceTimersByTimeAsync(249);
    expect(fn).not.toHaveBeenCalled();

    await vi.advanceTimersByTimeAsync(1);
    expect(fn).toHaveBeenCalledTimes(1);
    expect(fn).toHaveBeenCalledWith("tem");

    // Every waiter gets the latest result — never a stale answer.
    await expect(Promise.all([p1, p2, p3])).resolves.toEqual([
      "result:tem",
      "result:tem",
      "result:tem",
    ]);
  });

  it("runs again after the quiet period for a new query", async () => {
    const fn = vi.fn(async (q: string) => q.toUpperCase());
    const lookup = debounceAsync(fn, 100);

    const first = lookup("a");
    await vi.advanceTimersByTimeAsync(100);
    await expect(first).resolves.toBe("A");

    const second = lookup("b");
    await vi.advanceTimersByTimeAsync(100);
    await expect(second).resolves.toBe("B");
    expect(fn).toHaveBeenCalledTimes(2);
  });

  it("rejects every waiter when the lookup fails", async () => {
    const fn = vi.fn(async () => {
      throw new Error("boom");
    });
    const lookup = debounceAsync(fn, 50);

    const p1 = lookup("x");
    const p2 = lookup("y");
    // Attach handlers before advancing so the rejections are observed.
    const settled = Promise.allSettled([p1, p2]);
    await vi.advanceTimersByTimeAsync(50);
    const results = await settled;
    expect(results.map((r) => r.status)).toEqual(["rejected", "rejected"]);
    expect(fn).toHaveBeenCalledTimes(1);
  });
});
