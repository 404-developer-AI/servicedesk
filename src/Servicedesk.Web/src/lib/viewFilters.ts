import type { DisplayConfig, TicketListQuery, View } from "@/lib/ticket-api";

/// A saved view's stored filters and display config, translated to the
/// `GET /api/tickets` query. Shared by the desktop ticket list and the mobile
/// view list so both apply a view identically — the server still enforces
/// queue access on every request; this only decides what the view asks for.

/// v0.0.40 polish — pull a multi-id list from a parsed filtersJson object
/// while falling back to the legacy singular field. Returns undefined when
/// neither form is present so the caller can omit the key entirely.
export function readIdList(
  raw: Record<string, unknown>,
  listKey: string,
  singularKey: string,
): string[] | undefined {
  const list = raw[listKey];
  if (Array.isArray(list)) {
    const ids = list.filter((v): v is string => typeof v === "string" && v.length > 0);
    if (ids.length > 0) return ids;
  }
  const single = raw[singularKey];
  if (typeof single === "string" && single.length > 0) return [single];
  return undefined;
}

/// The view's filtersJson as list-query filters. Bad JSON → no filters
/// (the view shows unfiltered, as it always has).
export function viewFilters(view: Pick<View, "filtersJson">): TicketListQuery {
  try {
    const vf = JSON.parse(view.filtersJson) as Record<string, unknown>;
    const applied: TicketListQuery = {};
    // Multi-select arrays from the view editor. Legacy views (singular form)
    // are folded into a single-element array so the server can take the
    // same path.
    const queueIds = readIdList(vf, "queueIds", "queueId");
    const statusIds = readIdList(vf, "statusIds", "statusId");
    const priorityIds = readIdList(vf, "priorityIds", "priorityId");
    if (queueIds) applied.queueIds = queueIds;
    if (statusIds) applied.statusIds = statusIds;
    if (priorityIds) applied.priorityIds = priorityIds;
    if (typeof vf.assigneeUserId === "string") applied.assigneeUserId = vf.assigneeUserId;
    if (typeof vf.search === "string") applied.search = vf.search;
    if (vf.openOnly === true) applied.openOnly = true;
    if (vf.projectsOnly === true) applied.projectsOnly = true;
    if (vf.callbacksOnly === true) applied.callbacksOnly = true;
    if (vf.researchOnly === true) applied.researchOnly = true;
    return applied;
  } catch {
    return {};
  }
}

/// The view's display config (sorting, floats, search box). Bad or empty
/// JSON → defaults.
export function viewDisplayConfig(view: Pick<View, "displayConfigJson">): DisplayConfig {
  try {
    return view.displayConfigJson ? (JSON.parse(view.displayConfigJson) as DisplayConfig) : {};
  } catch {
    return {};
  }
}

/// Filters + display config folded into one list query (what the desktop
/// list sends, minus the column-dependent extras).
export function viewListQuery(filters: TicketListQuery, dc: DisplayConfig): TicketListQuery {
  return {
    ...filters,
    sortField: dc.sort?.field,
    sortDirection: dc.sort?.direction,
    priorityFloat: dc.priorityFloat,
    callbackFloat: dc.callbackFloat,
    researchFloat: dc.researchFloat,
    stateBucketSort: dc.stateBucketSort,
  };
}
