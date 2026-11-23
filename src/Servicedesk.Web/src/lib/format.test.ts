import { afterEach, describe, expect, it, vi } from "vitest";

const NOW = Date.parse("2026-03-10T12:00:00Z");
vi.mock("@/hooks/useServerTime", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/hooks/useServerTime")>()),
  serverNowMs: () => NOW,
}));

import {
  escapeHtml,
  formatApproxMinutes,
  formatBytes,
  formatDuration,
  formatMoney,
  formatNumber,
  formatTimeAgo,
  formatTimeFromNow,
} from "@/lib/format";

afterEach(() => vi.clearAllMocks());

describe("format", () => {
  it("formats byte sizes", () => {
    expect(formatBytes(512)).toBe("512 B");
    expect(formatBytes(3482)).toBe("3.4 KB");
    expect(formatBytes(5 * 1024 * 1024)).toBe("5.0 MB");
    expect(formatBytes(2.2 * 1024 ** 3)).toBe("2.20 GB");
    expect(formatBytes(-1)).toBe("");
  });

  it("formats time ago against server now", () => {
    expect(formatTimeAgo(NOW - 20_000)).toBe("just now");
    expect(formatTimeAgo(new Date(NOW - 5 * 60_000).toISOString())).toBe("5m ago");
    expect(formatTimeAgo(NOW - 3 * 3_600_000 - 59_000)).toBe("3h ago");
    expect(formatTimeAgo(NOW - 2 * 86_400_000)).toBe("2d ago");
    expect(formatTimeAgo(null)).toBe("—");
    expect(formatTimeAgo(undefined, { fallback: "never" })).toBe("never");
    expect(formatTimeAgo("not a date")).toBe("—");
    expect(formatTimeAgo(NOW - 40 * 86_400_000, { dateAfterDays: 30 })).not.toMatch(/ago$/);
  });

  it("formats signed distances", () => {
    expect(formatTimeFromNow(30_000)).toBe("now");
    expect(formatTimeFromNow(5 * 60_000)).toBe("in 5m");
    expect(formatTimeFromNow(-5 * 60_000)).toBe("5m ago");
    expect(formatTimeFromNow(-3 * 3_600_000)).toBe("3h ago");
    expect(formatTimeFromNow(2 * 86_400_000)).toBe("in 2d");
  });

  it("formats exact and approximate durations in English units", () => {
    expect(formatDuration(45)).toBe("45m");
    expect(formatDuration(120)).toBe("2h");
    expect(formatDuration(150)).toBe("2h 30m");
    expect(formatDuration(0)).toBe("0m");
    expect(formatDuration(null, "—")).toBe("—");
    expect(formatDuration(0, "0h")).toBe("0h");
    expect(formatDuration(90, "0h")).toBe("1h 30m");
    expect(formatApproxMinutes(45)).toBe("45m");
    expect(formatApproxMinutes(90)).toBe("1.5h");
    expect(formatApproxMinutes(2880)).toBe("2.0d");
    expect(formatApproxMinutes(null)).toBe("—");
  });

  it("formats money and decimals", () => {
    expect(formatMoney(null)).toBe("—");
    expect(formatMoney(12.5)).toContain("12,50");
    expect(formatNumber(3.5)).toBe("3,5");
    expect(formatNumber(1)).toBe("1");
  });

  it("escapes HTML", () => {
    expect(escapeHtml(`<a href="x">Tom & 'Jerry'</a>`)).toBe(
      "&lt;a href=&quot;x&quot;&gt;Tom &amp; &#39;Jerry&#39;&lt;/a&gt;",
    );
  });
});
