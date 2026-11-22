import { useQuery } from "@tanstack/react-query";
import { taxonomyApi } from "@/lib/api";

/// v0.1.61 — the soft lock: is this ticket's category "Locked" (handled via
/// Back office forms)? UI only — never an access rule; the server accepts
/// every edit as before.
export function useCategoryLocked(categoryId: string | null | undefined): { locked: boolean; name: string | null } {
  const { data } = useQuery({
    queryKey: ["taxonomy", "categories"],
    queryFn: () => taxonomyApi.categories.list(),
    staleTime: 300_000,
  });
  const cat = categoryId ? data?.find((c) => c.id === categoryId) : undefined;
  return { locked: !!cat?.isLocked, name: cat?.name ?? null };
}
