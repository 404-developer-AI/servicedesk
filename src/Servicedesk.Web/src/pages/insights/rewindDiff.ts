import type { RewindItem, RewindSnapshot } from "@/lib/insights-api";

// v0.1.31 — Rewind: what changed between two snapshots of a view. Pure, so
// it is unit-tested on its own (rewindDiff.test.ts).

export type ItemChange = { label: string; from: string; to: string };

export type DiffRow = {
  item: RewindItem;
  position: number;
  isNew: boolean;
  /** + = moved up (closer to the top of its group). */
  moved: number;
  fromGroup: string | null;
  changes: ItemChange[];
};

export type SnapshotDiff = {
  hasCompare: boolean;
  rows: Map<string, DiffRow>;
  gone: Array<{ item: RewindItem; groupLabel: string }>;
  groupDelta: Map<string, number>;
  newCount: number;
  movedCount: number;
  changedCount: number;
};

export function countByGroup(s: RewindSnapshot): Map<string, number> {
  const m = new Map<string, number>();
  for (const i of s.items) m.set(i.group, (m.get(i.group) ?? 0) + 1);
  return m;
}

/// Rank of each ticket among the tickets of its group that are in the
/// *other* snapshot in the same group too — so a ticket leaving or arriving
/// never counts as everyone below it "moving".
function sharedRanks(s: RewindSnapshot, other: Map<string, RewindItem>): Map<string, number> {
  const seen = new Map<string, number>();
  const rank = new Map<string, number>();
  for (const i of s.items) {
    if (other.get(i.id)?.group !== i.group) continue;
    const n = (seen.get(i.group) ?? 0) + 1;
    seen.set(i.group, n);
    rank.set(i.id, n);
  }
  return rank;
}

function positions(s: RewindSnapshot): Map<string, number> {
  const seen = new Map<string, number>();
  const pos = new Map<string, number>();
  for (const i of s.items) {
    const n = (seen.get(i.group) ?? 0) + 1;
    seen.set(i.group, n);
    pos.set(i.id, n);
  }
  return pos;
}

const flagText = (i: RewindItem) =>
  [i.isCallback && "Call-back", i.isResearch && "Research"].filter(Boolean).join(", ") || "none";

export function diffSnapshots(cur: RewindSnapshot, prev: RewindSnapshot | null): SnapshotDiff {
  const curPos = positions(cur);
  const prevById = new Map((prev?.items ?? []).map((i) => [i.id, i]));
  const curById = new Map(cur.items.map((i) => [i.id, i]));
  const curRank = sharedRanks(cur, prevById);
  const prevRank = prev ? sharedRanks(prev, curById) : new Map<string, number>();
  const prevGroupLabel = new Map((prev?.groups ?? []).map((g) => [g.key, g.label]));
  const curIds = new Set(cur.items.map((i) => i.id));

  const rows = new Map<string, DiffRow>();
  let newCount = 0;
  let movedCount = 0;
  let changedCount = 0;
  for (const item of cur.items) {
    const position = curPos.get(item.id)!;
    const before = prevById.get(item.id);
    if (!prev || !before) {
      if (prev) newCount++;
      rows.set(item.id, { item, position, isNew: !!prev, moved: 0, fromGroup: null, changes: [] });
      continue;
    }
    const changes: ItemChange[] = [];
    if (before.subject !== item.subject) changes.push({ label: "Subject", from: before.subject, to: item.subject });
    if (before.statusId !== item.statusId) changes.push({ label: "Status", from: before.statusName, to: item.statusName });
    if (before.priorityName !== item.priorityName) changes.push({ label: "Priority", from: before.priorityName, to: item.priorityName });
    if (before.assigneeUserId !== item.assigneeUserId)
      changes.push({ label: "Assignee", from: before.assigneeEmail ?? "nobody", to: item.assigneeEmail ?? "nobody" });
    if (before.isCallback !== item.isCallback || before.isResearch !== item.isResearch)
      changes.push({ label: "Flags", from: flagText(before), to: flagText(item) });
    const sameGroup = before.group === item.group;
    const moved = sameGroup ? prevRank.get(item.id)! - curRank.get(item.id)! : 0;
    if (moved !== 0 || !sameGroup) movedCount++;
    if (changes.length > 0) changedCount++;
    rows.set(item.id, {
      item,
      position,
      isNew: false,
      moved,
      fromGroup: sameGroup ? null : prevGroupLabel.get(before.group) ?? "another group",
      changes,
    });
  }

  const gone = (prev?.items ?? [])
    .filter((i) => !curIds.has(i.id))
    .map((item) => ({ item, groupLabel: prevGroupLabel.get(item.group) ?? "" }));

  const groupDelta = new Map<string, number>();
  if (prev) {
    const a = countByGroup(cur);
    const b = countByGroup(prev);
    for (const key of new Set([...a.keys(), ...b.keys()])) groupDelta.set(key, (a.get(key) ?? 0) - (b.get(key) ?? 0));
  }

  return { hasCompare: !!prev, rows, gone, groupDelta, newCount, movedCount, changedCount };
}

