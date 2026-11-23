import type { SettingEntry } from "@/lib/api";

export function findEntry(entries: SettingEntry[] | undefined, key: string) {
  return entries?.find((e) => e.key === key);
}
