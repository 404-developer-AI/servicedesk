export const ROLES = ["Customer", "Agent", "Admin"] as const;
export type Role = (typeof ROLES)[number];

