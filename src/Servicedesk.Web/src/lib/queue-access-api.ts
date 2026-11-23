import { request } from "@/lib/http";
import type { Queue } from "@/lib/taxonomy-api";

// ---- Agent-Facing Queue List (scoped to accessible queues) ----

export const agentQueueApi = {
  list: () => request<Queue[]>("GET", "/api/queues"),
};
// ---- Queue Access Admin ----

export const queueAccessApi = {
  getForUser: (userId: string) =>
    request<{ queueIds: string[] }>("GET", `/api/admin/queue-access/${userId}`),
  setForUser: (userId: string, queueIds: string[]) =>
    request<void>("PUT", `/api/admin/queue-access/${userId}`, { queueIds }),
  getByQueue: (queueId: string) =>
    request<{ userIds: string[] }>("GET", `/api/admin/queue-access/by-queue/${queueId}`),
};
