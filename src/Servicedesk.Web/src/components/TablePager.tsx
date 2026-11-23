import { Button } from "@/components/ui/button";

/// "Page X of Y · <detail>" with Previous/Next. Callers decide when to show it.
export function TablePager({
  page,
  totalPages,
  detail,
  busy = false,
  onPageChange,
}: {
  page: number;
  totalPages: number;
  detail: string;
  busy?: boolean;
  onPageChange: (updater: (page: number) => number) => void;
}) {
  return (
    <div className="flex items-center justify-between text-xs text-muted-foreground">
      <span>
        Page {page} of {totalPages} · {detail}
      </span>
      <div className="flex items-center gap-2">
        <Button
          size="sm"
          variant="ghost"
          onClick={() => onPageChange((p) => Math.max(1, p - 1))}
          disabled={page <= 1 || busy}
        >
          Previous
        </Button>
        <Button
          size="sm"
          variant="ghost"
          onClick={() => onPageChange((p) => Math.min(totalPages, p + 1))}
          disabled={page >= totalPages || busy}
        >
          Next
        </Button>
      </div>
    </div>
  );
}
