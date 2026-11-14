import { formatDateTimeMedium } from "@/lib/dateFormat";
import { toast } from "sonner";
import { type SettingEntry } from "@/lib/api";

export const ADSOLUT_SETTINGS_QUERY_KEY = ["settings", "list", "Adsolut"] as const;
export const ADSOLUT_STATUS_QUERY_KEY = ["integrations", "adsolut", "status"] as const;
export const ADSOLUT_SECRET_QUERY_KEY = ["integrations", "adsolut", "secret"] as const;
export const ADSOLUT_ADMIN_QUERY_KEY = ["integrations", "adsolut", "administrations"] as const;
export const ADSOLUT_SYNC_QUERY_KEY = ["integrations", "adsolut", "sync"] as const;
export const ADSOLUT_COVERAGE_QUERY_KEY = ["integrations", "adsolut", "coverage"] as const;

export function findEntry(entries: SettingEntry[] | undefined, key: string) {
  return entries?.find((e) => e.key === key);
}

export function formatDate(iso: string | null | undefined) {
  if (!iso) return "—";
  return formatDateTimeMedium(iso);
}

export async function copyDebugBody(body: string) {
  try {
    await navigator.clipboard.writeText(body);
    toast.success("Response copied");
  } catch {
    toast.error("Could not copy — select the JSON manually");
  }
}
