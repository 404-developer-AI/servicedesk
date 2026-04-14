import { useEffect, useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { settingsApi, type SettingEntry } from "@/lib/api";
import { Input } from "@/components/ui/input";
import { cn } from "@/lib/utils";

function ToggleSwitch({
  checked,
  disabled,
  onChange,
}: {
  checked: boolean;
  disabled?: boolean;
  onChange: (v: boolean) => void;
}) {
  return (
    <button
      type="button"
      role="switch"
      aria-checked={checked}
      disabled={disabled}
      onClick={() => onChange(!checked)}
      className={cn(
        "relative inline-flex h-6 w-11 shrink-0 cursor-pointer rounded-full border-2 border-transparent transition-colors duration-200 ease-in-out",
        "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background",
        "disabled:cursor-not-allowed disabled:opacity-50",
        checked
          ? "bg-gradient-to-r from-violet-600 to-indigo-600"
          : "bg-white/[0.08]",
      )}
    >
      <span
        className={cn(
          "pointer-events-none inline-block h-5 w-5 rounded-full bg-white shadow-lg ring-0 transition-transform duration-200 ease-in-out",
          checked ? "translate-x-5" : "translate-x-0",
        )}
      />
    </button>
  );
}

type Props = {
  entry: SettingEntry;
  queryKey: readonly unknown[];
  label?: string;
  hint?: string;
  readOnly?: boolean;
};

export function SettingField({ entry, queryKey, label, hint, readOnly }: Props) {
  const qc = useQueryClient();
  const [draft, setDraft] = useState(entry.value);

  useEffect(() => {
    setDraft(entry.value);
  }, [entry.value]);

  const save = useMutation({
    mutationFn: (value: string) => settingsApi.update(entry.key, value),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey });
      toast.success(`${entry.key} updated`);
    },
    onError: () => {
      toast.error(`Failed to update ${entry.key}`);
      setDraft(entry.value);
    },
  });

  const isBool = entry.valueType === "bool";
  const isInt = entry.valueType === "int";

  const commit = (next: string) => {
    if (next === entry.value) return;
    save.mutate(next);
  };

  return (
    <div className="flex items-start justify-between gap-4 py-3 border-b border-white/[0.04] last:border-b-0">
      <div className="min-w-0 flex-1 space-y-1">
        <p className="text-sm font-medium text-foreground">{label ?? entry.key}</p>
        <p className="text-xs text-muted-foreground">{hint ?? entry.description}</p>
        <p className="text-[10px] uppercase tracking-wider text-muted-foreground/40 font-mono">
          {entry.key}
        </p>
      </div>
      <div className="shrink-0 flex items-center">
        {isBool ? (
          <ToggleSwitch
            checked={draft === "true"}
            disabled={readOnly || save.isPending}
            onChange={(v) => {
              const next = v ? "true" : "false";
              setDraft(next);
              commit(next);
            }}
          />
        ) : (
          <Input
            type={isInt ? "number" : "text"}
            value={draft}
            disabled={readOnly || save.isPending}
            onChange={(e) => setDraft(e.target.value)}
            onBlur={() => commit(draft)}
            onKeyDown={(e) => {
              if (e.key === "Enter") {
                e.currentTarget.blur();
              } else if (e.key === "Escape") {
                setDraft(entry.value);
                e.currentTarget.blur();
              }
            }}
            className="h-9 w-56 bg-white/[0.03] font-mono text-sm"
          />
        )}
      </div>
    </div>
  );
}
