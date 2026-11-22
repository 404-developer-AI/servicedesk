import type { ChipInput } from "./BackofficeStatusChip";

export function backofficeStatusLabel(item: ChipInput): string {
  if (item.status === "Open" && item.inProgressBy) return `In progress · ${item.inProgressBy}`;
  if (item.status === "Handled") return item.handledByName ? `Handled · ${item.handledByName}` : "Handled";
  return "Open";
}

/// Short display name for presence chips: the part before "@" of an e-mail,
/// first letter capitalised ("bram.vanbaelen@…" → "Bram.vanbaelen" is ugly,
/// so dots become spaces and each word is capitalised: "Bram Vanbaelen").
export function presenceName(name: string): string {
  const base = name.includes("@") ? name.slice(0, name.indexOf("@")) : name;
  return base
    .split(/[._-]+/)
    .filter(Boolean)
    .map((w) => w.charAt(0).toUpperCase() + w.slice(1))
    .join(" ");
}
