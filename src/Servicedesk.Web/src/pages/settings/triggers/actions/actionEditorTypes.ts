import type { Category, Queue, Status, Priority } from "@/lib/taxonomy-api";

export type ActionTaxonomies = {
  queues: Queue[];
  statuses: Status[];
  priorities: Priority[];
  categories?: Category[];
};
