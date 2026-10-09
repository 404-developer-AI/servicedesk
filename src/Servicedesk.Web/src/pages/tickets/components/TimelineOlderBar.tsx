import { ChevronsUp, Loader2 } from "lucide-react";

/// v0.1.33 — sits at the top of a paged ticket timeline when older entries
/// exist. "Load older" fetches one more page, "Load all" the rest. While the
/// in-ticket search is active it says the search only covers what is loaded.
export function TimelineOlderBar({
  loading,
  searching,
  onLoadOlder,
  onLoadAll,
}: {
  loading: boolean;
  searching: boolean;
  onLoadOlder: () => Promise<void>;
  onLoadAll: () => Promise<void>;
}) {
  return (
    <div className="sd-timeline-older mb-3 flex flex-wrap items-center justify-center gap-x-3 gap-y-1 rounded-md border border-dashed border-glass bg-glass px-3 py-2 text-xs text-muted-foreground">
      <span>
        {searching ? "Search covers the loaded entries only — older entries are not loaded." : "Older entries are not loaded."}
      </span>
      <span className="flex items-center gap-1">
        <button
          type="button"
          disabled={loading}
          onClick={() => void onLoadOlder()}
          className="inline-flex items-center gap-1 rounded px-2 py-0.5 font-medium text-foreground/80 transition-colors hover:bg-glass-hover hover:text-foreground disabled:opacity-50"
        >
          {loading ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <ChevronsUp className="h-3.5 w-3.5" />}
          Load older
        </button>
        <span aria-hidden className="text-muted-foreground/50">·</span>
        <button
          type="button"
          disabled={loading}
          onClick={() => void onLoadAll()}
          className="rounded px-2 py-0.5 font-medium text-foreground/80 transition-colors hover:bg-glass-hover hover:text-foreground disabled:opacity-50"
        >
          Load all
        </button>
      </span>
    </div>
  );
}
