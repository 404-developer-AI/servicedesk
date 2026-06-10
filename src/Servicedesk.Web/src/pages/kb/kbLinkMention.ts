import { kbApi } from "@/lib/kb-api";
import type { IntakeMentionItem } from "@/components/intake/IntakeMentionList";

// The enabled/baseUrl config only changes when an admin flips the setting,
// so don't refetch it on every `::` keystroke — cache it briefly.
const CONFIG_TTL_MS = 60_000;
let cachedConfig: { enabled: boolean; publicBaseUrl: string } | null = null;
let cachedAtMs = 0;

async function getConfig(): Promise<{ enabled: boolean; publicBaseUrl: string } | null> {
  if (cachedConfig && performance.now() - cachedAtMs < CONFIG_TTL_MS) return cachedConfig;
  try {
    cachedConfig = await kbApi.publicLinkConfig();
    cachedAtMs = performance.now();
    return cachedConfig;
  } catch {
    return null;
  }
}

/// Fetch Published KB articles for the `::` picker and map them to mention
/// items (kind: "kb") whose href is the article's public reader URL.
/// Only Published articles are requested (server-side status filter), so a
/// Draft/Internal article can never be linked by accident. Returns an empty
/// list when public links are disabled or on any failure, so the picker
/// degrades gracefully and nobody inserts a link that would 404.
export async function kbLinkMentionItems(query: string): Promise<IntakeMentionItem[]> {
  const config = await getConfig();
  if (!config?.enabled) return [];
  const base = config.publicBaseUrl || window.location.origin;
  try {
    const { items } = await kbApi.listArticles({
      status: "Published",
      search: query.trim() || undefined,
      pageSize: 8,
    });
    return items.map((a) => ({
      id: a.id,
      name: a.title,
      description: "Public article link",
      kind: "kb" as const,
      href: `${base}/kb/public/${a.id}`,
    }));
  } catch {
    return [];
  }
}
