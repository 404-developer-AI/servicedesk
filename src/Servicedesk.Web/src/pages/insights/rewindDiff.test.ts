import { describe, expect, it } from "vitest";
import type { RewindItem, RewindSnapshot } from "@/lib/insights-api";
import { diffSnapshots } from "./rewindDiff";

function item(n: number, group: string, over: Partial<RewindItem> = {}): RewindItem {
  return {
    id: `t${n}`, number: n, subject: `Ticket ${n}`, queueId: "q", queueName: "Servicedesk",
    statusId: "s-open", statusName: "Open", statusColor: "#888", stateCategory: "Open",
    priorityName: "Normal", priorityColor: "#999", priorityIsDefault: true,
    isCallback: false, isResearch: false, requester: "Alex Doe", companyName: null,
    assigneeUserId: null, assigneeEmail: null, createdUtc: "2026-10-01T08:00:00Z", pendingTillUtc: null,
    group, ...over,
  };
}

function snap(items: RewindItem[]): RewindSnapshot {
  const keys = [...new Set(items.map((i) => i.group))];
  return {
    slotUtc: "2026-10-08T11:00:00Z", covered: true, capturedUtc: "2026-10-08T11:00:00Z", truncated: false,
    groups: keys.map((k) => ({ key: k, label: k === "__all__" ? "All tickets" : k, color: null })),
    items, deletedIds: [],
  };
}

describe("diffSnapshots", () => {
  it("does not count tickets as moved when one above them leaves the group", () => {
    const prev = snap([item(1, "__all__"), item(2, "__all__"), item(3, "__all__")]);
    const cur = snap([item(1, "cb", { isCallback: true }), item(2, "__all__"), item(3, "__all__")]);

    const d = diffSnapshots(cur, prev);

    expect(d.movedCount).toBe(1); // only #1, which changed group
    expect(d.rows.get("t1")!.fromGroup).toBe("All tickets");
    expect(d.rows.get("t2")!.moved).toBe(0);
    expect(d.rows.get("t1")!.changes.map((c) => c.label)).toEqual(["Flags"]);
  });

  it("flags real reordering, new tickets, tickets that left and subject edits", () => {
    const prev = snap([item(1, "__all__"), item(2, "__all__"), item(3, "__all__")]);
    const cur = snap([item(3, "__all__"), item(1, "__all__", { subject: "Printer offline" }), item(4, "__all__")]);

    const d = diffSnapshots(cur, prev);

    expect(d.rows.get("t3")!.moved).toBe(1); // overtook #1
    expect(d.rows.get("t1")!.moved).toBe(-1);
    expect(d.rows.get("t4")!.isNew).toBe(true);
    expect(d.gone.map((g) => g.item.number)).toEqual([2]);
    expect(d.rows.get("t1")!.changes).toEqual([{ label: "Subject", from: "Ticket 1", to: "Printer offline" }]);
    expect(d.groupDelta.get("__all__")).toBe(0);
  });

  it("without a comparison nothing is new or moved", () => {
    const d = diffSnapshots(snap([item(1, "__all__")]), null);
    expect(d.hasCompare).toBe(false);
    expect(d.rows.get("t1")!.isNew).toBe(false);
  });
});
