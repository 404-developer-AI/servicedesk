import * as React from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import { settingsApi, type SettingEntry } from "@/lib/settings-api";
import { taxonomyApi, type Status } from "@/lib/taxonomy-api";
import { errorMessage } from "@/lib/http";
import { cn } from "@/lib/utils";
import { FieldRow } from "./timesheetSettingsShared";
import { QUERY_KEY } from "./timesheetSettingsKeys";

// ---- Status multi-select for the back-office tabs ---------------------

/// Stores a CSV of status ids. Statuses are toggled by name; the stored
/// value is the id so a rename keeps the selection. Several statuses can
/// share a state-category, so the picker lists every active status (the
/// category is shown only as a muted hint).
/// v0.1.32 — also used by Settings → General → Insights (Workflow WFP
/// statuses); `queryKey` is the settings query to refresh after a save.
export function StatusSetField({
  entry,
  label,
  hint,
  queryKey = QUERY_KEY,
}: {
  entry: SettingEntry;
  label: string;
  hint: string;
  queryKey?: readonly unknown[];
}) {
  const qc = useQueryClient();
  const statusesQuery = useQuery({
    queryKey: ["taxonomy", "statuses"],
    queryFn: () => taxonomyApi.statuses.list(),
    staleTime: 60_000,
  });
  const statuses = (statusesQuery.data ?? []).filter((s) => s.isActive);

  const [draft, setDraft] = React.useState<Set<string>>(() => parseIdCsv(entry.value));
  React.useEffect(() => setDraft(parseIdCsv(entry.value)), [entry.value]);

  const save = useMutation({
    mutationFn: (val: string) => settingsApi.update(entry.key, val),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey });
      toast.success(`${label} updated`);
    },
    onError: (err) => toast.error(errorMessage(err, "Failed to save")),
  });

  const dirty = idCsvFromSet(draft) !== normaliseIdCsv(entry.value);

  const toggle = (id: string) => {
    const next = new Set(draft);
    if (next.has(id)) next.delete(id);
    else next.add(id);
    setDraft(next);
  };

  return (
    <FieldRow label={label} hint={hint}>
      <div className="flex max-w-md flex-col items-end gap-2">
        {statusesQuery.isLoading ? (
          <Skeleton className="h-8 w-64" />
        ) : statuses.length === 0 ? (
          <p className="text-xs text-muted-foreground">No statuses defined.</p>
        ) : (
          <div className="flex flex-wrap justify-end gap-1.5">
            {statuses.map((s) => (
              <StatusChip key={s.id} status={s} on={draft.has(s.id)} onToggle={() => toggle(s.id)} disabled={save.isPending} />
            ))}
          </div>
        )}
        <div className="flex items-center gap-2">
          <Button
            size="sm"
            variant="ghost"
            disabled={!dirty || save.isPending}
            onClick={() => save.mutate(idCsvFromSet(draft))}
            className="h-8 px-3"
          >
            Save
          </Button>
          {dirty && (
            <Button
              size="sm"
              variant="ghost"
              disabled={save.isPending}
              onClick={() => setDraft(parseIdCsv(entry.value))}
              className="h-8 px-2 text-xs text-muted-foreground"
            >
              Reset
            </Button>
          )}
        </div>
      </div>
    </FieldRow>
  );
}

function StatusChip({
  status,
  on,
  onToggle,
  disabled,
}: {
  status: Status;
  on: boolean;
  onToggle: () => void;
  disabled: boolean;
}) {
  return (
    <button
      type="button"
      onClick={onToggle}
      disabled={disabled}
      aria-pressed={on}
      title={status.stateCategory}
      className={cn(
        "inline-flex items-center gap-1.5 rounded-full border px-2.5 py-1 text-xs font-medium transition-colors",
        "disabled:cursor-not-allowed disabled:opacity-50",
        on
          ? "border-violet-400/40 bg-violet-400/15 text-foreground"
          : "border-glass bg-glass text-muted-foreground hover:bg-glass-hover",
      )}
    >
      <span
        className="h-2 w-2 shrink-0 rounded-full"
        style={{ backgroundColor: status.color }}
        aria-hidden
      />
      {status.name}
      <span className="text-[9px] uppercase tracking-wider text-muted-foreground/50">
        {status.stateCategory}
      </span>
    </button>
  );
}

function parseIdCsv(csv: string): Set<string> {
  const out = new Set<string>();
  for (const part of csv.split(",").map((s) => s.trim())) {
    if (part) out.add(part);
  }
  return out;
}

function idCsvFromSet(set: Set<string>): string {
  return [...set].sort().join(",");
}

function normaliseIdCsv(csv: string): string {
  return idCsvFromSet(parseIdCsv(csv));
}
