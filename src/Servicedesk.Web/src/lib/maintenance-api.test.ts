import { describe, expect, it } from "vitest";
import { mailDateOf, scheduleLabel } from "./maintenance-api";
import { minuteToTime, timeToMinute } from "./timeOfDay";

describe("maintenance time helpers", () => {
  it("round-trips a time of day", () => {
    expect(minuteToTime(540)).toBe("09:00");
    expect(minuteToTime(1439)).toBe("23:59");
    expect(timeToMinute("09:00")).toBe(540);
    expect(timeToMinute("7:05")).toBe(425);
  });
  it("rejects invalid times", () => {
    expect(timeToMinute("24:00")).toBeNull();
    expect(timeToMinute("12:60")).toBeNull();
    expect(timeToMinute("")).toBeNull();
    expect(minuteToTime(Number.NaN)).toBe("—");
  });
  it("labels the schedule", () => {
    expect(scheduleLabel({ scheduleMode: "Manual", intervalMonths: null })).toBe("Manual");
    expect(scheduleLabel({ scheduleMode: "Automatic", intervalMonths: 6 })).toBe("Every 6 months");
    expect(scheduleLabel({ scheduleMode: "Automatic", intervalMonths: 1 })).toBe("Every 1 month");
  });
  it("computes the mail day across month and year ends", () => {
    expect(mailDateOf("2026-12-09", 14)).toBe("2026-11-25");
    expect(mailDateOf("2027-01-05", 14)).toBe("2026-12-22");
    expect(mailDateOf("2028-03-01", 1)).toBe("2028-02-29");
    expect(mailDateOf(null, 14)).toBeNull();
  });
});
