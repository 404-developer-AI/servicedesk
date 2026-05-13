import * as React from "react";
import { Check } from "lucide-react";
import { cn } from "@/lib/utils";

/// v0.0.37 — "Pending till" datetime-local input shared by the ticket
/// side panel (edit flow) and the new-ticket drawer (create flow).
/// The stored value is a UTC ISO string; the input shows the browser's
/// local clock interpretation. Round-trips minute-precision (seconds
/// are dropped — agents pick "tomorrow 09:00", not "09:00:43").
///
/// Save semantics: edits are kept LOCAL until the user confirms via
/// blur (clicking elsewhere) or the explicit ✓ button. This prevents
/// each keystroke from firing a PATCH and a noisy "Ticket updated"
/// toast. `onCommit` fires once with the final value.
export function PendingTillField({
  value,
  onCommit,
  className,
}: {
  /** Stored value (UTC ISO string) or null. */
  value: string | null;
  /** Fires with a UTC ISO string when the user confirms, or null when the user clears. */
  onCommit: (isoUtcOrNull: string | null) => void;
  className?: string;
}) {
  const savedLocal = React.useMemo(() => formatLocalForInput(value), [value]);
  // Local draft so live edits don't spam PATCHes. Reseeded whenever
  // the saved value changes (e.g. after refetch lands).
  const [draft, setDraft] = React.useState(savedLocal);
  React.useEffect(() => setDraft(savedLocal), [savedLocal]);
  const dirty = draft !== savedLocal;

  const commit = () => {
    if (!dirty) return;
    if (!draft) {
      onCommit(null);
      return;
    }
    const parsed = new Date(draft);
    if (isNaN(parsed.getTime())) {
      // Bad input — snap back to the saved value so the field never
      // displays a value that diverges from the source of truth.
      setDraft(savedLocal);
      return;
    }
    onCommit(parsed.toISOString());
  };

  return (
    <div className="flex items-center gap-1.5">
      <input
        type="datetime-local"
        value={draft}
        onChange={(e) => setDraft(e.target.value)}
        onBlur={commit}
        onKeyDown={(e) => {
          if (e.key === "Enter") {
            e.preventDefault();
            (e.currentTarget as HTMLInputElement).blur();
          } else if (e.key === "Escape") {
            e.preventDefault();
            setDraft(savedLocal);
          }
        }}
        className={cn(
          "w-full h-9 px-2 text-sm rounded-md border border-white/10 bg-white/[0.04] text-foreground outline-none focus:border-primary/60",
          dirty && "border-emerald-400/40 ring-1 ring-emerald-400/30",
          className,
        )}
      />
      {dirty && (
        <button
          type="button"
          onClick={commit}
          className="inline-flex h-8 w-8 shrink-0 items-center justify-center rounded-md border border-emerald-400/40 bg-emerald-400/10 text-emerald-200 hover:bg-emerald-400/20"
          title="Save"
          aria-label="Save Pending till"
        >
          <Check className="h-3.5 w-3.5" />
        </button>
      )}
    </div>
  );
}

function formatLocalForInput(isoUtc: string | null): string {
  if (!isoUtc) return "";
  const d = new Date(isoUtc);
  if (isNaN(d.getTime())) return "";
  const pad = (n: number) => n.toString().padStart(2, "0");
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
}
