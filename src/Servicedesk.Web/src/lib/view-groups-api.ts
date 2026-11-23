import { request } from "@/lib/http";

// ---- View Groups Admin ----

export type ViewGroupSummary = {
  id: string;
  name: string;
  description: string;
  sortOrder: number;
  memberCount: number;
  viewCount: number;
  createdUtc: string;
  updatedUtc: string;
};
export type ViewGroupMember = {
  userId: string;
  email: string;
};
export type ViewGroupView = {
  viewId: string;
  viewName: string;
  sortOrder: number;
};
export type ViewGroupDetail = {
  group: ViewGroupSummary;
  members: ViewGroupMember[];
  views: ViewGroupView[];
};
export type ViewGroupInput = {
  name: string;
  description?: string;
  sortOrder?: number;
};
export const viewGroupApi = {
  list: () => request<ViewGroupSummary[]>("GET", "/api/admin/view-groups"),
  get: (id: string) => request<ViewGroupDetail>("GET", `/api/admin/view-groups/${id}`),
  create: (input: ViewGroupInput) =>
    request<ViewGroupSummary>("POST", "/api/admin/view-groups", input),
  update: (id: string, input: ViewGroupInput) =>
    request<ViewGroupSummary>("PUT", `/api/admin/view-groups/${id}`, input),
  remove: (id: string) => request<void>("DELETE", `/api/admin/view-groups/${id}`),
  setMembers: (id: string, userIds: string[]) =>
    request<void>("PUT", `/api/admin/view-groups/${id}/members`, { userIds }),
  setViews: (id: string, viewIds: string[]) =>
    request<void>("PUT", `/api/admin/view-groups/${id}/views`, { viewIds }),
};
