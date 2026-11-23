import * as React from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { settingsApi, type SettingEntry } from "@/lib/settings-api";
import { errorMessage } from "@/lib/http";
import { cn } from "@/lib/utils";
import { FieldRow } from "./timesheetSettingsShared";
import { QUERY_KEY } from "./timesheetSettingsKeys";

const WEEKDAYS: { iso: number; label: string }[] = [
  { iso: 1, label: "Mon" },
  { iso: 2, label: "Tue" },
  { iso: 3, label: "Wed" },
  { iso: 4, label: "Thu" },
  { iso: 5, label: "Fri" },
  { iso: 6, label: "Sat" },
  { iso: 7, label: "Sun" },
];

// ---- Weekday-checkbox group for the work-days setting -----------------

export function WorkDaysField({ entry }: { entry: SettingEntry }) {
  const qc = useQueryClient();
  const [draft, setDraft] = React.useState<Set<number>>(() => parseCsv(entry.value));
  React.useEffect(() => setDraft(parseCsv(entry.value)), [entry.value]);

  const save = useMutation({
    mutationFn: (val: string) => settingsApi.update(entry.key, val),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: QUERY_KEY });
      toast.success("Work days updated");
    },
    onError: (err) => toast.error(errorMessage(err, "Failed to save")),
  });

  const dirty = csvFromSet(draft) !== entry.value;

  const toggle = (iso: number) => {
    const next = new Set(draft);
    if (next.has(iso)) next.delete(iso);
    else next.add(iso);
    setDraft(next);
  };

  return (
    <FieldRow
      label="Work days"
      hint="Toggle a day to include or exclude it from the 'Not filled' detection on Tab 3."
    >
      <div className="flex flex-wrap items-center gap-2">
        {WEEKDAYS.map((d) => {
          const on = draft.has(d.iso);
          return (
            <button
              key={d.iso}
              type="button"
              onClick={() => toggle(d.iso)}
              disabled={save.isPending}
              className={cn(
                "inline-flex h-9 w-12 items-center justify-center rounded-md border text-xs font-medium transition-colors",
                "disabled:cursor-not-allowed disabled:opacity-50",
                on
                  ? "border-violet-400/40 bg-violet-400/15 text-violet-200 hover:bg-violet-400/20"
                  : "border-glass bg-glass text-muted-foreground hover:bg-glass-hover",
              )}
              aria-pressed={on}
            >
              {d.label}
            </button>
          );
        })}
        <Button
          size="sm"
          variant="ghost"
          disabled={!dirty || save.isPending}
          onClick={() => save.mutate(csvFromSet(draft))}
          className="h-9 px-3"
        >
          Save
        </Button>
        {dirty && (
          <Button
            size="sm"
            variant="ghost"
            disabled={save.isPending}
            onClick={() => setDraft(parseCsv(entry.value))}
            className="h-9 px-2 text-xs text-muted-foreground"
          >
            Reset
          </Button>
        )}
      </div>
    </FieldRow>
  );
}

function parseCsv(csv: string): Set<number> {
  const out = new Set<number>();
  for (const part of csv.split(",").map((s) => s.trim())) {
    const n = Number.parseInt(part, 10);
    if (Number.isFinite(n) && n >= 1 && n <= 7) out.add(n);
  }
  return out;
}

function csvFromSet(set: Set<number>): string {
  return [...set].sort((a, b) => a - b).join(",");
}
