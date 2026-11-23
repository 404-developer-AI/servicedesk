import * as React from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Input } from "@/components/ui/input";
import { Switch } from "@/components/ui/switch";
import { settingsApi, type SettingEntry } from "@/lib/settings-api";
import { errorMessage } from "@/lib/http";
import { autoFormatTimeInput, formatHHMM, parseHHMM } from "@/lib/timesheet-api";
import { cn } from "@/lib/utils";
import { formatDuration } from "@/lib/format";
import { FieldRow } from "./timesheetSettingsShared";
import { QUERY_KEY } from "./timesheetSettingsKeys";

// ---- HH:MM field for the day-start setting ----------------------------

export function TimeOfDayField({
  entry,
  label = "Start time of a new day",
  hint = "The Start column on the first row of every new day is pre-filled with this value.",
}: {
  entry: SettingEntry;
  label?: string;
  hint?: string;
}) {
  const qc = useQueryClient();
  const initialMinutes = Number.parseInt(entry.value, 10);
  const initialText = Number.isFinite(initialMinutes) ? formatHHMM(initialMinutes) : "08:30";
  const [text, setText] = React.useState(initialText);
  const [error, setError] = React.useState<string | null>(null);

  React.useEffect(() => {
    setText(initialText);
  }, [initialText]);

  const save = useMutation({
    mutationFn: (minutes: number) =>
      settingsApi.update(entry.key, String(minutes)),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: QUERY_KEY });
      toast.success(`${label} updated`);
    },
    onError: (err) =>
      toast.error(errorMessage(err, "Failed to save")),
  });

  const commit = () => {
    const parsed = parseHHMM(text.trim());
    if (parsed === null) {
      setError("Enter a time as HH:MM (e.g. 08:30).");
      return;
    }
    setError(null);
    setText(formatHHMM(parsed));
    if (parsed !== initialMinutes) save.mutate(parsed);
  };

  return (
    <FieldRow label={label} hint={hint}>
      <Input
        value={text}
        onChange={(e) => {
          setText(autoFormatTimeInput(e.target.value));
          setError(null);
        }}
        onBlur={commit}
        onKeyDown={(e) => {
          if (e.key === "Enter") (e.target as HTMLInputElement).blur();
        }}
        placeholder="HH:MM"
        className={cn(
          "h-9 w-32 font-mono",
          error && "border-red-400/60",
        )}
        disabled={save.isPending}
      />
      {error && <p className="mt-1 text-[10px] text-red-300">{error}</p>}
    </FieldRow>
  );
}

// ---- Generic minutes field with "= Xh Ym" hint ------------------------

export function MinutesField({
  entry,
  label,
  hint,
}: {
  entry: SettingEntry;
  label: string;
  hint: string;
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

  const minutes = Number.parseInt(draft, 10);
  const hourSummary =
    Number.isFinite(minutes) && minutes >= 0
      ? formatDuration(minutes, "0h")
      : "—";

  const commit = () => {
    if (draft === entry.value) return;
    const parsed = Number.parseInt(draft, 10);
    if (!Number.isFinite(parsed) || parsed < 0) {
      toast.error("Must be a non-negative whole number of minutes");
      setDraft(entry.value);
      return;
    }
    save.mutate(String(parsed));
  };

  return (
    <FieldRow label={label} hint={hint}>
      <div className="flex items-center gap-3">
        <Input
          type="number"
          min={0}
          value={draft}
          onChange={(e) => setDraft(e.target.value)}
          onBlur={commit}
          onKeyDown={(e) => {
            if (e.key === "Enter") (e.target as HTMLInputElement).blur();
          }}
          className="h-9 w-32 font-mono"
          disabled={save.isPending}
        />
        <span className="text-xs text-muted-foreground">= {hourSummary}</span>
      </div>
    </FieldRow>
  );
}

// ---- Boolean toggle field (saves "true"/"false") ----------------------

export function ToggleField({
  entry,
  label,
  hint,
}: {
  entry: SettingEntry;
  label: string;
  hint: string;
}) {
  const qc = useQueryClient();
  const checked = entry.value === "true";

  const save = useMutation({
    mutationFn: (val: boolean) => settingsApi.update(entry.key, String(val)),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: QUERY_KEY });
      toast.success(`${label} updated`);
    },
    onError: (err) =>
      toast.error(errorMessage(err, "Failed to save")),
  });

  return (
    <FieldRow label={label} hint={hint}>
      <Switch
        checked={checked}
        disabled={save.isPending}
        onCheckedChange={(v) => save.mutate(v)}
      />
    </FieldRow>
  );
}

// ---- Multi-line text field (full-width) -------------------------------

export function TextAreaField({
  entry,
  label,
  hint,
}: {
  entry: SettingEntry;
  label: string;
  hint: string;
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

  const commit = () => {
    const trimmed = draft.trim();
    if (trimmed === entry.value) return;
    if (trimmed.length === 0) {
      toast.error("This text cannot be empty");
      setDraft(entry.value);
      return;
    }
    save.mutate(trimmed);
  };

  return (
    <div className="border-b border-glass py-3 last:border-b-0">
      <p className="text-sm font-medium text-foreground">{label}</p>
      <p className="mt-0.5 mb-2 text-xs text-muted-foreground">{hint}</p>
      <textarea
        value={draft}
        onChange={(e) => setDraft(e.target.value)}
        onBlur={commit}
        rows={2}
        className="w-full rounded-md border border-glass bg-glass px-3 py-2 text-sm text-foreground outline-hidden focus-visible:ring-2 focus-visible:ring-ring disabled:opacity-60"
        disabled={save.isPending}
      />
    </div>
  );
}

// ---- Hourly-rate (money) field ----------------------------------------

export function HourlyRateField({ entry }: { entry: SettingEntry }) {
  const qc = useQueryClient();
  const [draft, setDraft] = React.useState(entry.value);
  React.useEffect(() => setDraft(entry.value), [entry.value]);

  const save = useMutation({
    mutationFn: (val: string) => settingsApi.update(entry.key, val),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: QUERY_KEY });
      toast.success("Hourly rate updated");
    },
    onError: (err) => {
      toast.error(errorMessage(err, "Failed to save"));
      setDraft(entry.value);
    },
  });

  // Accept "75", "75.50" or the Belgian "75,50"; normalise to a dot for storage.
  const parseRate = (raw: string): number | null => {
    const n = Number.parseFloat(raw.trim().replace(",", "."));
    return Number.isFinite(n) && n >= 0 ? n : null;
  };

  const commit = () => {
    if (draft === entry.value) return;
    const parsed = parseRate(draft);
    if (parsed === null) {
      toast.error("Enter a non-negative amount, e.g. 75 or 75.50");
      setDraft(entry.value);
      return;
    }
    save.mutate(String(parsed));
  };

  return (
    <FieldRow
      label="Gross hourly rate (EUR)"
      hint="Used for the Bruto Price column. Enter a number like 75 or 75.50 (comma also accepted). 0 = column stays blank."
    >
      <div className="flex items-center gap-2">
        <span className="text-sm text-muted-foreground">€</span>
        <Input
          inputMode="decimal"
          value={draft}
          onChange={(e) => setDraft(e.target.value)}
          onBlur={commit}
          onKeyDown={(e) => {
            if (e.key === "Enter") (e.target as HTMLInputElement).blur();
          }}
          className="h-9 w-32 font-mono"
          placeholder="0"
          disabled={save.isPending}
        />
      </div>
    </FieldRow>
  );
}
