import { describe, expect, it, vi, beforeEach } from "vitest";
import { act, renderHook } from "@testing-library/react";

const clock = vi.hoisted(() => ({ nowMs: null as number | null, zone: "Europe/Brussels" as string | null }));
vi.mock("@/hooks/useServerTime", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/hooks/useServerTime")>()),
  useServerNowMs: () => clock.nowMs,
  useServerTimeZone: () => clock.zone,
}));

import { useMonthCursor, useServerYearMonth } from "./useServerMonth";

describe("useServerYearMonth", () => {
  beforeEach(() => {
    clock.nowMs = null;
    clock.zone = "Europe/Brussels";
  });

  it("is null before the first time sync", () => {
    const { result } = renderHook(() => useServerYearMonth());
    expect(result.current).toBeNull();
  });

  it("takes the month in the server zone, not UTC or the browser's", () => {
    // 30 Nov 23:30 UTC is already 1 December in Brussels.
    clock.nowMs = Date.UTC(2026, 10, 30, 23, 30);
    const { result } = renderHook(() => useServerYearMonth());
    expect(result.current).toEqual({ year: 2026, month: 12 });
  });
});

describe("useMonthCursor", () => {
  beforeEach(() => {
    clock.nowMs = Date.UTC(2026, 11, 15, 12, 0);
    clock.zone = "Europe/Brussels";
  });

  it("follows the current server month until the user steps", () => {
    const { result, rerender } = renderHook(() => useMonthCursor());
    expect(result.current.ym).toEqual({ year: 2026, month: 12 });

    // The server month rolls over while the page stays open.
    clock.nowMs = Date.UTC(2027, 0, 2, 12, 0);
    rerender();
    expect(result.current.ym).toEqual({ year: 2027, month: 1 });
  });

  it("steps across a year boundary and resets to the current month", () => {
    const { result } = renderHook(() => useMonthCursor());

    act(() => result.current.step(1));
    expect(result.current.ym).toEqual({ year: 2027, month: 1 });
    act(() => result.current.step(-2));
    expect(result.current.ym).toEqual({ year: 2026, month: 11 });

    act(() => result.current.reset());
    expect(result.current.ym).toEqual({ year: 2026, month: 12 });
  });
});
