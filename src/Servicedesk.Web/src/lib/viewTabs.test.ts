import { describe, expect, it } from "vitest";
import { OTHER_TAB, countReadyByTab, filterByTab, isReadyWork, tabOf } from "./viewTabs";

const PLAN = "cat-plan";
const FINISH = "cat-finish";
const NOW = Date.parse("2026-11-21T10:00:00Z");

const t = (categoryId: string | null, statusStateCategory: string, pendingTillUtc: string | null = null) =>
  ({ categoryId, statusStateCategory, pendingTillUtc });

describe("view tabs", () => {
  it("puts a ticket in its category tab, everything else in Other", () => {
    expect(tabOf(t(PLAN, "Open"), [PLAN, FINISH])).toBe(PLAN);
    expect(tabOf(t("cat-unlisted", "Open"), [PLAN, FINISH])).toBe(OTHER_TAB);
    expect(tabOf(t(null, "Open"), [PLAN])).toBe(OTHER_TAB);
  });

  it("counts only ready work: open tickets and overdue pending ones", () => {
    expect(isReadyWork(t(PLAN, "Open"), NOW)).toBe(true);
    expect(isReadyWork(t(PLAN, "New"), NOW)).toBe(true);
    expect(isReadyWork(t(PLAN, "Pending", "2026-11-22T08:00:00Z"), NOW)).toBe(false);
    expect(isReadyWork(t(PLAN, "Pending", "2026-11-20T08:00:00Z"), NOW)).toBe(true);
    expect(isReadyWork(t(PLAN, "Pending"), NOW)).toBe(false);
    expect(isReadyWork(t(PLAN, "Resolved"), NOW)).toBe(false);
  });

  it("builds per-tab badges and filters rows", () => {
    const items = [
      t(PLAN, "Open"),
      t(PLAN, "Pending", "2026-11-25T08:00:00Z"),
      t(FINISH, "Pending", "2026-11-25T08:00:00Z"),
      t(null, "Open"),
      t(null, "Pending", "2026-11-25T08:00:00Z"),
    ];
    expect(countReadyByTab(items, [PLAN, FINISH], NOW)).toEqual({ [OTHER_TAB]: 1, [PLAN]: 1, [FINISH]: 0 });
    expect(filterByTab(items, PLAN, [PLAN, FINISH])).toHaveLength(2);
    expect(filterByTab(items, OTHER_TAB, [PLAN, FINISH])).toHaveLength(2);
  });
});
