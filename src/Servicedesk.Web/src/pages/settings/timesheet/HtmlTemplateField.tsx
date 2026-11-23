import * as React from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { settingsApi, type SettingEntry } from "@/lib/settings-api";
import { errorMessage } from "@/lib/http";
import { cn } from "@/lib/utils";
import { QUERY_KEY } from "./timesheetSettingsKeys";

// ---- HTML-template textarea field -------------------------------------

export function HtmlTemplateField({
  entry,
  label,
  hint,
  placeholders,
}: {
  entry: SettingEntry;
  label: string;
  hint: string;
  placeholders: string[];
}) {
  const qc = useQueryClient();
  const [draft, setDraft] = React.useState(entry.value);
  React.useEffect(() => setDraft(entry.value), [entry.value]);

  const save = useMutation({
    mutationFn: (val: string) => settingsApi.update(entry.key, val),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: QUERY_KEY });
      toast.success(`${label} updated`);
    },
    onError: (err) => {
      toast.error(errorMessage(err, "Failed to save"));
      setDraft(entry.value);
    },
  });

  const dirty = draft !== entry.value;

  return (
    <div className="border-b border-glass py-3 last:border-b-0 space-y-2">
      <div className="flex items-start justify-between gap-4">
        <div className="min-w-0 flex-1">
          <p className="text-sm font-medium text-foreground">{label}</p>
          <p className="mt-0.5 text-xs text-muted-foreground">{hint}</p>
        </div>
        <div className="shrink-0 flex items-center gap-2">
          <Button
            size="sm"
            variant="ghost"
            disabled={!dirty || save.isPending}
            onClick={() => save.mutate(draft)}
            className="h-8 px-3"
          >
            Save
          </Button>
          {dirty && (
            <Button
              size="sm"
              variant="ghost"
              disabled={save.isPending}
              onClick={() => setDraft(entry.value)}
              className="h-8 px-2 text-xs text-muted-foreground"
            >
              Reset
            </Button>
          )}
        </div>
      </div>
      <textarea
        value={draft}
        onChange={(e) => setDraft(e.target.value)}
        disabled={save.isPending}
        rows={4}
        spellCheck={false}
        className={cn(
          "w-full rounded-md border border-glass bg-glass px-3 py-2",
          "font-mono text-[11px] leading-relaxed text-foreground/90",
          "focus:outline-hidden focus:ring-1 focus:ring-violet-400/40 focus:border-violet-400/40",
          "disabled:opacity-50",
        )}
      />
      {placeholders.length > 0 && (
        <div className="flex flex-wrap items-center gap-1">
          <span className="text-[10px] uppercase tracking-wider text-muted-foreground/70">
            Placeholders:
          </span>
          {placeholders.map((p) => (
            <code
              key={p}
              className="rounded bg-glass px-1.5 py-0.5 font-mono text-[10px] text-muted-foreground"
            >
              {p}
            </code>
          ))}
        </div>
      )}
    </div>
  );
}
