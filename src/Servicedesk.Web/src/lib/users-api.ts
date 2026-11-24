import { request } from "@/lib/http";

// ---- Users ----

export type AgentUser = {
  id: string;
  email: string;
  roleName: string;
};

export const userApi = {
  listAgents: () => request<AgentUser[]>("GET", "/api/users"),
  /// Typeahead for the @@-mention popover. Agent + Admin only; customers
  /// never appear. Empty `q` returns the top-N alphabetically.
  searchAgents: (q: string, limit = 20) => {
    const params = new URLSearchParams();
    if (q) params.set("q", q);
    params.set("limit", String(limit));
    return request<AgentUser[]>("GET", `/api/users/agents/search?${params.toString()}`);
  },
};
