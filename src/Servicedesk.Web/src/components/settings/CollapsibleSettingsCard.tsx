import * as React from "react";
import { useQuery } from "@tanstack/react-query";
import { ChevronDown, ChevronRight } from "lucide-react";
import { settingsApi, type SettingEntry } from "@/lib/api";
import { Skeleton } from "@/components/ui/skeleton";
import { SettingField } from "@/components/settings/SettingField";

/// Collapsed-by-default glass card that lazily loads one settings category
/// and renders an ordered subset of its keys through SettingField.
/// Unknown keys (not registered on the server) simply do not render, so a
/// page can list a key ahead of the backend shipping it. Introduced for
/// Settings → Health (v0.0.18 security activity, v0.0.99 polling) and
/// reused for Settings → Triggers (v0.0.100 scheduler & retention).
export function CollapsibleSettingsCard({
  icon,
  title,
  description,
  category,
  keys,
  iconClassName = "text-primary",
}: {
  icon: React.ReactNode;
  title: string;
  description: string;
  /// Settings category passed to `settingsApi.list` (also the query key).
  category: string;
  keys: ReadonlyArray<{ key: string; label: string }>;
  iconClassName?: string;
}) {
  const [open, setOpen] = React.useState(false);
  const queryKey = React.useMemo(() => ["settings", "list", category] as const, [category]);
  const settings = useQuery({
    queryKey,
    queryFn: () => settingsApi.list(category),
    enabled: open,
  });

  const entriesByKey = React.useMemo(() => {
    const m = new Map<string, SettingEntry>();
    for (const e of settings.data ?? []) m.set(e.key, e);
    return m;
  }, [settings.data]);

  return (
    <section className="rounded-lg border border-glass-strong bg-glass">
      <button
        type="button"
        onClick={() => setOpen((v) => !v)}
        className="flex w-full items-center gap-3 px-5 py-4 text-left"
      >
        <div className={`rounded-md bg-glass p-2 ${iconClassName}`}>{icon}</div>
        <div className="min-w-0 flex-1">
          <h2 className="text-sm font-semibold text-foreground">{title}</h2>
          <p className="text-xs text-muted-foreground">{description}</p>
        </div>
        {open ? (
          <ChevronDown className="h-4 w-4 text-muted-foreground" />
        ) : (
          <ChevronRight className="h-4 w-4 text-muted-foreground" />
        )}
      </button>
      {open ? (
        <div className="border-t border-glass px-5 py-4">
          {settings.isLoading ? (
            <Skeleton className="h-24 w-full" />
          ) : settings.isError ? (
            <p className="text-sm text-muted-foreground">Failed to load settings.</p>
          ) : (
            <div className="flex flex-col">
              {keys.map(({ key, label }) => {
                const entry = entriesByKey.get(key);
                if (!entry) return null;
                return <SettingField key={key} entry={entry} queryKey={queryKey} label={label} />;
              })}
            </div>
          )}
        </div>
      ) : null}
    </section>
  );
}
