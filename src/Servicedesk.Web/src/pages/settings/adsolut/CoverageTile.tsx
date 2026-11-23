import { Link } from "@tanstack/react-router";
import { ArrowRight, CheckCircle2 } from "lucide-react";
import type { AdsolutCoverageCounts } from "@/lib/adsolut-api";
import { Skeleton } from "@/components/ui/skeleton";
import { cn } from "@/lib/utils";

type CoverageTileProps = {
  counts: AdsolutCoverageCounts | undefined;
  isLoading: boolean;
  pushUpdateOn: boolean;
  pushContactsUpdateOn: boolean;
};

/// v0.0.30 — surfaces the five gap-buckets between SD's local universe
/// and Adsolut. Each bucket renders as a click-through "View →" amber pill
/// when non-zero, or rolls into a "Fully covered" green rest-state when
/// every category is zero. Drift counts are suppressed when the relevant
/// push-toggle is ON because the push-tak resolves them within one tick;
/// the rest are visible regardless of toggles so an admin can see what
/// would *not* sync if they were to turn the toggles on.
export function CoverageTile({
  counts,
  isLoading,
  pushUpdateOn,
  pushContactsUpdateOn,
}: CoverageTileProps) {
  if (isLoading || !counts) {
    return (
      <div className="rounded-md border border-glass-strong bg-glass p-4">
        <div className="mb-2 text-xs font-medium uppercase tracking-widest text-muted-foreground/70">
          Sync coverage
        </div>
        <Skeleton className="h-6 w-32" />
      </div>
    );
  }

  // Drift buckets are suppressed when the relevant push-update toggle is
  // ON; in that case the push-tak resolves within one tick. The other
  // buckets stay visible regardless because the push-tak doesn't auto-link
  // pre-existing rows, and pure-SD contacts will never appear upstream
  // unless an admin links them by hand.
  const companiesDriftDisplay = pushUpdateOn ? 0 : counts.companiesDrift;
  const contactLinksDriftDisplay = pushContactsUpdateOn ? 0 : counts.contactLinksDrift;

  type CellSearch = {
    tab: "companies" | "contacts";
    bucket: string;
    search: undefined;
    page: undefined;
  };
  const cells: Array<{
    label: string;
    count: number;
    search: CellSearch;
    hint?: string;
  }> = [
    {
      label: "Companies — SD only",
      count: counts.companiesSdOnly,
      search: { tab: "companies", bucket: "sd-only", search: undefined, page: undefined },
    },
    {
      label: "Companies — drift",
      count: companiesDriftDisplay,
      search: { tab: "companies", bucket: "drift", search: undefined, page: undefined },
      hint: pushUpdateOn ? "Resolved by push-tak (toggle ON)" : undefined,
    },
    {
      label: "Contact-links — unsynced",
      count: counts.contactLinksUnsynced,
      search: { tab: "contacts", bucket: "links-unsynced", search: undefined, page: undefined },
    },
    {
      label: "Contact-links — drift",
      count: contactLinksDriftDisplay,
      search: { tab: "contacts", bucket: "links-drift", search: undefined, page: undefined },
      hint: pushContactsUpdateOn ? "Resolved by push-tak (toggle ON)" : undefined,
    },
    {
      label: "Contacts — pure SD",
      count: counts.contactsPureSd,
      search: { tab: "contacts", bucket: "pure-sd", search: undefined, page: undefined },
    },
  ];

  const totalGaps = cells.reduce((sum, c) => sum + c.count, 0);

  return (
    <div className="rounded-md border border-glass-strong bg-glass p-4">
      <div className="mb-3 flex items-center justify-between gap-3">
        <div className="text-xs font-medium uppercase tracking-widest text-muted-foreground/70">
          Sync coverage
        </div>
        {totalGaps === 0 ? (
          <span className="inline-flex items-center gap-1.5 rounded-full border border-emerald-400/30 bg-emerald-500/10 px-2.5 py-0.5 text-[11px] text-emerald-300">
            <CheckCircle2 className="h-3 w-3" />
            Fully covered
          </span>
        ) : (
          <Link
            to="/settings/integrations/adsolut/coverage"
            search={{
              tab: "companies",
              bucket: undefined,
              search: undefined,
              page: undefined,
            }}
            className="inline-flex items-center gap-1 text-[11px] text-muted-foreground hover:text-foreground"
          >
            Open coverage page
            <ArrowRight className="h-3 w-3" />
          </Link>
        )}
      </div>
      <div className="grid grid-cols-1 gap-2 sm:grid-cols-2 lg:grid-cols-3">
        {cells.map((c) => (
          <Link
            key={c.label}
            to="/settings/integrations/adsolut/coverage"
            search={c.search}
            className={cn(
              "group flex items-center justify-between rounded-md border px-3 py-2 transition-colors",
              c.count === 0
                ? "border-glass-strong bg-glass text-muted-foreground/70 hover:text-muted-foreground"
                : "border-amber-400/30 bg-amber-500/6 text-amber-200 hover:bg-amber-500/[0.10]",
            )}
          >
            <span className="flex flex-col">
              <span className="text-[11px]">{c.label}</span>
              {c.hint && (
                <span className="text-[10px] text-muted-foreground/60">{c.hint}</span>
              )}
            </span>
            <span className="flex items-center gap-1.5 tabular-nums">
              <span className="text-sm font-medium">{c.count}</span>
              <ArrowRight
                className={cn(
                  "h-3 w-3",
                  c.count === 0
                    ? "opacity-0 group-hover:opacity-30"
                    : "opacity-50 group-hover:opacity-90",
                )}
              />
            </span>
          </Link>
        ))}
      </div>
    </div>
  );
}
