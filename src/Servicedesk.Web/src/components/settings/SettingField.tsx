import { useEffect, useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Info, RotateCcw } from "lucide-react";
import { settingsApi, type SettingEntry } from "@/lib/api";
import { Input } from "@/components/ui/input";
import {
  Tooltip,
  TooltipContent,
  TooltipProvider,
  TooltipTrigger,
} from "@/components/ui/tooltip";
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
          : "bg-glass-strong",
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

  const isModified = entry.value !== entry.defaultValue;
  const resetToDefault = () => {
    setDraft(entry.defaultValue);
    commit(entry.defaultValue);
  };

  // Description-on-hover: the verbose hint paragraph used to render
  // inline as muted-grey text under the label, which made dense settings
  // panels (Adsolut integration, mail, …) feel noisy. We keep the copy
  // available — server-managed via SettingDefault.description, so it
  // stays editable without touching this file — but only show it on
  // hover of the small Info-icon next to the label.
  const description = hint ?? entry.description;
  const hasDescription = !!description && description.trim().length > 0;

  return (
    <div className="flex items-start justify-between gap-4 py-3 border-b border-glass last:border-b-0">
      <div className="min-w-0 flex-1 space-y-1">
        <div className="flex items-center gap-1.5">
          <p className="text-sm font-medium text-foreground">{label ?? entry.key}</p>
          {hasDescription && (
            <TooltipProvider delayDuration={150}>
              <Tooltip>
                <TooltipTrigger asChild>
                  <button
                    type="button"
                    aria-label="Show description"
                    className="inline-flex h-4 w-4 items-center justify-center rounded-full text-muted-foreground/40 transition-colors hover:text-muted-foreground/80 focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
                  >
                    <Info className="h-3 w-3" />
                  </button>
                </TooltipTrigger>
                <TooltipContent
                  side="right"
                  align="start"
                  className="max-w-sm whitespace-normal border border-glass bg-popover/95 text-xs leading-relaxed text-muted-foreground shadow-xl backdrop-blur"
                >
                  {description}
                </TooltipContent>
              </Tooltip>
            </TooltipProvider>
          )}
        </div>
        <p className="text-[10px] uppercase tracking-wider text-muted-foreground/40 font-mono">
          {entry.key}
        </p>
      </div>
      <div className="shrink-0 flex items-center gap-2">
        {isModified && !readOnly && (
          <TooltipProvider delayDuration={150}>
            <Tooltip>
              <TooltipTrigger asChild>
                <button
                  type="button"
                  aria-label="Reset to default"
                  disabled={save.isPending}
                  onClick={resetToDefault}
                  className="inline-flex h-7 w-7 items-center justify-center rounded-md text-muted-foreground/50 transition-colors hover:bg-glass hover:text-foreground focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring disabled:cursor-not-allowed disabled:opacity-50"
                >
                  <RotateCcw className="h-3.5 w-3.5" />
                </button>
              </TooltipTrigger>
              <TooltipContent
                side="left"
                className="border border-glass bg-popover/95 text-xs text-muted-foreground shadow-xl backdrop-blur"
              >
                Reset to default
              </TooltipContent>
            </Tooltip>
          </TooltipProvider>
        )}
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
            className="h-9 w-56 bg-glass font-mono text-sm"
          />
        )}
      </div>
    </div>
  );
}
