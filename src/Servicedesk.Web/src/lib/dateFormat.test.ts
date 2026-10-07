import { describe, expect, it } from "vitest";
import {
  formatDate,
  formatDateMedium,
  formatDateTime,
  formatDateTimeMedium,
  formatDayMonthTime,
  formatMonthYear,
  formatTime,
} from "@/lib/dateFormat";

const BRU = "Europe/Brussels";

describe("formatDateTime", () => {
  it("converts a UTC instant to Brussels summer time (+2)", () => {
    expect(formatDateTime("2026-10-07T18:52:00Z", BRU)).toBe("07/10/2026 - 20:52");
  });

  it("converts a UTC instant to Brussels winter time (+1)", () => {
    expect(formatDateTime("2026-12-07T18:52:00Z", BRU)).toBe("07/12/2026 - 19:52");
  });

  it("appends seconds on request", () => {
    expect(formatDateTime("2026-10-07T18:52:05Z", BRU, true)).toBe("07/10/2026 - 20:52:05");
  });

  it("rolls the date over across midnight", () => {
    expect(formatDateTime("2026-10-07T22:30:00Z", BRU)).toBe("08/10/2026 - 00:30");
  });

  it("returns the fallback for null, empty and invalid input", () => {
    expect(formatDateTime(null, BRU)).toBe("—");
    expect(formatDateTime("", BRU)).toBe("—");
    expect(formatDateTime("not a date", BRU)).toBe("—");
    expect(formatDateTime(undefined, BRU, false, "never")).toBe("never");
  });

  it("does not throw on an unknown zone id", () => {
    expect(() => formatDateTime("2026-10-07T18:52:00Z", "Mars/Olympus")).not.toThrow();
    expect(formatDateTime("2026-10-07T18:52:00Z", "Mars/Olympus")).toMatch(/^\d{2}\/\d{2}\/\d{4} - \d{2}:\d{2}$/);
  });
});

describe("date-only values", () => {
  it.each([BRU, "America/Los_Angeles", "Pacific/Auckland"])("are not shifted in %s", (zone) => {
    expect(formatDate("2026-10-07", zone)).toBe("07/10/2026");
    expect(formatDateMedium("2026-10-07", zone)).toBe("07 Oct 2026");
  });
});

describe("other formatters", () => {
  it("formatDate uses the zone-local calendar day", () => {
    expect(formatDate("2026-10-07T22:30:00Z", BRU)).toBe("08/10/2026");
  });

  it("formatDateMedium uses English month abbreviations", () => {
    expect(formatDateMedium("2027-03-23T12:00:00Z", BRU)).toBe("23 Mar 2027");
    expect(formatDateMedium("2026-10-07T12:00:00Z", BRU)).toBe("07 Oct 2026");
  });

  it("formatDateTimeMedium and formatDayMonthTime", () => {
    expect(formatDateTimeMedium("2026-10-07T18:52:00Z", BRU)).toBe("07 Oct 2026, 20:52");
    expect(formatDayMonthTime("2026-10-07T18:52:00Z", BRU)).toBe("07 Oct, 20:52");
  });

  it("formatTime", () => {
    expect(formatTime("2026-10-07T18:52:05Z", BRU)).toBe("20:52");
    expect(formatTime("2026-10-07T18:52:05Z", BRU, true)).toBe("20:52:05");
  });

  it("formatMonthYear takes a 1-based month", () => {
    expect(formatMonthYear(2026, 10)).toBe("October 2026");
    expect(formatMonthYear(2027, 1)).toBe("January 2027");
  });
});
