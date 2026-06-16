import * as React from "react";
import { DateTimePicker } from "@/components/ui/datetime-picker";
import { useServerTime } from "@/hooks/useServerTime";

/// "Pending till" date+time picker shared by the ticket side panel (edit flow)
/// and the new-ticket drawer (create flow). Wraps the glass DateTimePicker so
/// both flows get the same controlled popover with an explicit "Done" button.
/// The old native datetime-local control had no clear way to confirm/close the
/// browser calendar, and that calendar indicator was swallowed inside the vaul
/// drawer. Works in server-local time via the server offset — the client never
/// trusts its own timezone. Round-trips a UTC ISO string (or null).
///
/// Commit timing: by default (edit flow) the value is buffered locally and
/// committed once when the popover closes, so adjusting date + hour + minute
/// fires a single PATCH instead of one per interaction. With commitOnChange
/// (the new-ticket drawer, where onCommit only updates local form state) every
/// change commits immediately.
export function PendingTillField({
  value,
  onCommit,
  className,
  commitOnChange = false,
}: {
  /** Stored value (UTC ISO string) or null. */
  value: string | null;
  /** Fires with a UTC ISO string when the user confirms, or null when cleared. */
  onCommit: (isoUtcOrNull: string | null) => void;
  className?: string;
  /**
   * When true, every change commits immediately. Use this where `onCommit`
   * only updates local form state (the new-ticket drawer) — there is no PATCH
   * to debounce. Defaults to the edit-flow behaviour: commit once on close.
   */
  commitOnChange?: boolean;
}) {
  const { time } = useServerTime();
  const offsetMinutes = time?.offsetMinutes ?? 0;

  // Buffered draft for the edit flow so in-popover date/time tweaks commit as a
  // single PATCH when the popover closes. Reseeded whenever the saved value
  // changes (e.g. after a refetch lands).
  const [draft, setDraft] = React.useState<string | null>(value);
  React.useEffect(() => setDraft(value), [value]);

  if (commitOnChange) {
    return (
      <DateTimePicker
        value={value}
        offsetMinutes={offsetMinutes}
        onChange={onCommit}
        placeholder="Select date & time"
        className={className}
      />
    );
  }

  return (
    <DateTimePicker
      value={draft}
      offsetMinutes={offsetMinutes}
      onChange={(v) => {
        setDraft(v);
        // Clearing (the trigger's ✕) happens while the popover is closed, so it
        // would never reach onClose — commit it straight away. In-popover edits
        // buffer and commit together when the popover closes.
        if (v === null) onCommit(null);
      }}
      onClose={() => {
        if (draft !== value) onCommit(draft);
      }}
      placeholder="Select date & time"
      className={className}
    />
  );
}
