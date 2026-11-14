import { describe, expect, it } from "vitest";
import { markedSeen, withoutTicket, type FollowedTicket, type FollowingResponse } from "./following-api";

const item = (id: string, hasActivity: boolean): FollowedTicket => ({
  ticketId: id, ticketNumber: 1, subject: "Printer offline", statusName: "Open", statusColor: "#3b82f6",
  statusCategory: "Open", followedUtc: "2026-11-14T10:00:00Z", lastSeenUtc: "2026-11-14T10:00:00Z",
  lastActivityUtc: "2026-11-14T11:00:00Z", hasActivity,
});

const data: FollowingResponse = { count: 2, activeCount: 1, items: [item("a", true), item("b", false)] };

describe("following cache helpers", () => {
  it("unfollowing drops the row and recounts", () => {
    expect(withoutTicket(data, "a")).toEqual({ count: 1, activeCount: 0, items: [item("b", false)] });
  });

  it("marking seen clears only that ticket's marker", () => {
    const next = markedSeen(data, "a")!;
    expect(next.activeCount).toBe(0);
    expect(next.count).toBe(2);
    expect(next.items.every((i) => !i.hasActivity)).toBe(true);
  });

  it("marking a ticket without news returns the same object", () => {
    expect(markedSeen(data, "b")).toBe(data);
  });
});
