import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { memoizeTtl } from "@/lib/ttlCache";

describe("memoizeTtl", () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date("2026-09-09T08:54:00Z"));
  });
  afterEach(() => {
    vi.useRealTimers();
  });

  it("serves repeated calls with the same arguments from cache within the TTL", async () => {
    const fn = vi.fn(async (queueId: string | null) => [`tpl-for-${queueId ?? "none"}`]);
    const cached = memoizeTtl(fn, 30_000);

    await expect(cached("q1")).resolves.toEqual(["tpl-for-q1"]);
    await expect(cached("q1")).resolves.toEqual(["tpl-for-q1"]);
    await expect(cached(null)).resolves.toEqual(["tpl-for-none"]);
    expect(fn).toHaveBeenCalledTimes(2); // one per distinct key
  });

  it("shares one in-flight request between concurrent callers", async () => {
    let resolve!: (v: string[]) => void;
    const fn = vi.fn((_key: string) => new Promise<string[]>((r) => { resolve = r; }));
    const cached = memoizeTtl(fn, 30_000);

    const a = cached("q");
    const b = cached("q");
    expect(fn).toHaveBeenCalledTimes(1);
    resolve(["x"]);
    await expect(Promise.all([a, b])).resolves.toEqual([["x"], ["x"]]);
  });

  it("refetches once the TTL has elapsed", async () => {
    const fn = vi.fn(async (_key: string) => ["v"]);
    const cached = memoizeTtl(fn, 30_000);

    await cached("q");
    vi.setSystemTime(new Date("2026-09-09T08:54:31Z"));
    await cached("q");
    expect(fn).toHaveBeenCalledTimes(2);
  });

  it("evicts a failed lookup so the next call retries", async () => {
    const fn = vi
      .fn<() => Promise<string[]>>()
      .mockRejectedValueOnce(new Error("429"))
      .mockResolvedValueOnce(["ok"]);
    const cached = memoizeTtl(fn, 30_000);

    await expect(cached()).rejects.toThrow("429");
    await expect(cached()).resolves.toEqual(["ok"]);
    expect(fn).toHaveBeenCalledTimes(2);
  });

  it("invalidate() drops every entry", async () => {
    const fn = vi.fn(async (_key: string) => ["v"]);
    const cached = memoizeTtl(fn, 30_000);

    await cached("q");
    cached.invalidate();
    await cached("q");
    expect(fn).toHaveBeenCalledTimes(2);
  });
});
