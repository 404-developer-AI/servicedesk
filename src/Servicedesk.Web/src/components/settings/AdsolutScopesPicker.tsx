import { useMemo, useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { AlertTriangle, Code2, Lock } from "lucide-react";
import { ApiError, settingsApi, type SettingEntry } from "@/lib/api";
import { Button } from "@/components/ui/button";
import { cn } from "@/lib/utils";

/// One Adsolut scope with the metadata the picker renders. `required` scopes
/// are always present in the saved string and rendered checked + disabled
/// — the admin literally cannot break the connect-flow by un-ticking them.
/// `recommended` scopes default ON for the v0.0.26 happy path; `optional`
/// scopes default OFF and surface a forward-pointing hint about when they
/// become useful.
type ScopeCatalogEntry = {
  name: string;
  label: string;
  description: string;
  group: "required" | "recommended" | "optional";
};

const SCOPE_CATALOG: readonly ScopeCatalogEntry[] = [
  {
    name: "openid",
    label: "OpenID Connect",
    description:
      "Mints the id_token used to read who authorized the connection (the email shown under Authorized as).",
    group: "required",
  },
  {
    name: "offline_access",
    label: "Offline access",
    description:
      "Issues the long-lived refresh token. Without this every call requires a fresh interactive login.",
    group: "required",
  },
  {
    name: "WK.BE.Administrations",
    label: "Administrations",
    description:
      "Lists Adsolut dossiers (administrations) and activates the integration on the picked one. Without this the dossier picker has nothing to show.",
    group: "required",
  },
  {
    name: "WK.BE.Accounting.Read",
    label: "Accounting (read)",
    description:
      "Lists Customers, Suppliers and other Accounting data. Required for the Companies pull (v0.0.26+).",
    group: "required",
  },
  {
    name: "WK.BE.Accounting.Write",
    label: "Accounting (write)",
    description:
      "Required for the v0.0.27 Companies push (POST/PUT /customers). Without it the push-tak comes back with 403 from Wolters Kluwer on every write attempt. Promoted from optional to required in v0.0.27 — installs upgrading from v0.0.26 get this scope appended automatically and need to reconnect to bind it to a fresh refresh token.",
    group: "required",
  },
  {
    name: "profile",
    label: "Profile",
    description:
      "Lets the id_token carry the authorizing user's display name + email. Cosmetic — you'll see 'Connected as <subject>' instead of '<email>' without it.",
    group: "recommended",
  },
] as const;

const REQUIRED_SCOPES = SCOPE_CATALOG.filter((s) => s.group === "required").map(
  (s) => s.name,
);
const KNOWN_SCOPES = new Set(SCOPE_CATALOG.map((s) => s.name));

/// Parse a saved space-separated scopes string into a set, tolerating
/// double spaces and stray whitespace — same shape Wolters Kluwer accepts.
function parseScopes(raw: string): Set<string> {
  return new Set(
    raw
      .split(/\s+/)
      .map((s) => s.trim())
      .filter((s) => s.length > 0),
  );
}

/// Serialise the picker's checked state back to the canonical space-separated
/// string. Required scopes are always included regardless of UI state — the
/// disabled checkboxes are presentation only; the saved value never lacks them.
function serialiseScopes(checked: Set<string>, raw: string | null): string {
  const fromRaw = raw ? parseScopes(raw) : new Set<string>();
  const final = new Set<string>(REQUIRED_SCOPES);
  for (const s of checked) {
    if (KNOWN_SCOPES.has(s)) final.add(s);
  }
  // Preserve unknown scopes the admin may have pasted via the advanced
  // editor — a future WK scope shouldn't disappear after a save round-trip.
  for (const s of fromRaw) {
    if (!KNOWN_SCOPES.has(s)) final.add(s);
  }
  return Array.from(final).join(" ");
}

type Props = {
  entry: SettingEntry | undefined;
  /** React Query key for the settings list to invalidate on save. */
  queryKey: readonly unknown[];
  /** True when the saved value already differs from the active refresh-token's scope set. */
  needsReconnect: boolean;
};

export function AdsolutScopesPicker({ entry, queryKey, needsReconnect }: Props) {
  const qc = useQueryClient();
  const savedRaw = entry?.value ?? "";
  const savedSet = useMemo(() => parseScopes(savedRaw), [savedRaw]);

  const [checked, setChecked] = useState<Set<string>>(() => new Set(savedSet));
  const [advancedOpen, setAdvancedOpen] = useState(false);
  const [rawDraft, setRawDraft] = useState<string>(savedRaw);

  // Re-sync when the upstream entry changes (e.g. another admin saved on
  // another tab and SignalR refetched the settings query).
  useMemo(() => {
    setChecked(new Set(savedSet));
    setRawDraft(savedRaw);
  }, [savedRaw, savedSet]);

  const dirty = useMemo(() => {
    if (advancedOpen) return rawDraft.trim() !== savedRaw.trim();
    const next = serialiseScopes(checked, savedRaw);
    return parseScopesEqual(parseScopes(next), savedSet) === false;
  }, [advancedOpen, checked, rawDraft, savedRaw, savedSet]);

  const save = useMutation({
    mutationFn: (value: string) =>
      settingsApi.update(entry?.key ?? "", value),
    onSuccess: () => {
      toast.success("Scopes saved — reconnect to bind them to a fresh token.");
      qc.invalidateQueries({ queryKey });
      qc.invalidateQueries({
        queryKey: ["integrations", "adsolut", "status"],
      });
    },
    onError: (err) =>
      toast.error(
        err instanceof ApiError ? `Save failed (${err.status})` : "Save failed",
      ),
  });

  if (!entry) {
    return (
      <div className="rounded-md border border-rose-400/30 bg-rose-500/[0.08] px-3 py-2 text-xs text-rose-200">
        Could not load Adsolut.Scopes setting.
      </div>
    );
  }

  const grouped = {
    required: SCOPE_CATALOG.filter((s) => s.group === "required"),
    recommended: SCOPE_CATALOG.filter((s) => s.group === "recommended"),
    optional: SCOPE_CATALOG.filter((s) => s.group === "optional"),
  };

  // Unknown scopes lurking in the saved string (forward-compat or hand-edits)
  // — surface as a read-only chip-row so the admin sees them without losing
  // them on a save round-trip.
  const unknown = Array.from(savedSet).filter((s) => !KNOWN_SCOPES.has(s));

  function toggle(name: string) {
    setChecked((prev) => {
      const next = new Set(prev);
      if (next.has(name)) next.delete(name);
      else next.add(name);
      return next;
    });
  }

  function onSavePicker() {
    save.mutate(serialiseScopes(checked, savedRaw));
  }

  function onSaveAdvanced() {
    save.mutate(rawDraft.trim());
  }

  return (
    <div className="rounded-md border border-white/[0.06] bg-white/[0.02]">
      <header className="flex items-start justify-between gap-3 border-b border-white/[0.04] px-4 py-3">
        <div className="space-y-0.5">
          <div className="text-sm font-medium text-foreground">OAuth scopes</div>
          <div className="text-xs text-muted-foreground">
            Permissions Wolters Kluwer grants to this servicedesk install.
            Required scopes can't be turned off — disabling one would break
            the connect or sync flow.
          </div>
        </div>
        {needsReconnect && (
          <span className="inline-flex shrink-0 items-center gap-1.5 whitespace-nowrap rounded-full border border-amber-400/40 bg-amber-500/[0.12] px-2.5 py-0.5 text-xs text-amber-200">
            <AlertTriangle className="h-3 w-3" />
            Reconnect required
          </span>
        )}
      </header>

      {!advancedOpen ? (
        <div className="space-y-4 px-4 py-4">
          <ScopeGroup
            title="Required"
            subtitle="Always granted; needed for v0.0.27 bidirectional Companies sync."
            entries={grouped.required}
            checked={checked}
            disabled
            onToggle={toggle}
          />
          <ScopeGroup
            title="Recommended"
            subtitle="Default on. Cosmetic UX — safe to disable."
            entries={grouped.recommended}
            checked={checked}
            onToggle={toggle}
          />
          {grouped.optional.length > 0 && (
            <ScopeGroup
              title="Optional"
              subtitle="Default off. Forward-compat for upcoming versions."
              entries={grouped.optional}
              checked={checked}
              onToggle={toggle}
            />
          )}

          {unknown.length > 0 && (
            <div className="rounded-md border border-white/[0.06] bg-white/[0.02] px-3 py-2 text-xs">
              <div className="mb-1.5 text-muted-foreground/70">
                Other scopes (preserved on save)
              </div>
              <div className="flex flex-wrap gap-1.5">
                {unknown.map((s) => (
                  <code
                    key={s}
                    className="rounded bg-white/[0.04] px-1.5 py-0.5 font-mono text-[11px]"
                  >
                    {s}
                  </code>
                ))}
              </div>
            </div>
          )}
        </div>
      ) : (
        <div className="space-y-3 px-4 py-4">
          <div className="rounded-md border border-amber-400/30 bg-amber-500/[0.08] px-3 py-2 text-[11px] text-amber-200">
            Advanced — paste a raw space-separated scope list. Use this only
            when Wolters Kluwer has documented a scope the picker doesn't
            know about yet. Unknown scopes are preserved as-is on save.
          </div>
          <textarea
            value={rawDraft}
            onChange={(e) => setRawDraft(e.target.value)}
            rows={3}
            className="w-full rounded-md border border-white/10 bg-white/[0.04] px-3 py-2 font-mono text-xs"
            placeholder="openid offline_access ..."
            spellCheck={false}
          />
        </div>
      )}

      <footer className="flex items-center justify-between gap-2 border-t border-white/[0.04] bg-white/[0.01] px-4 py-2.5">
        <Button
          size="sm"
          variant="ghost"
          onClick={() => setAdvancedOpen((v) => !v)}
          className="gap-1.5 text-xs text-muted-foreground"
        >
          <Code2 className="h-3 w-3" />
          {advancedOpen ? "Hide raw editor" : "Advanced — paste raw"}
        </Button>
        <div className="flex items-center gap-2">
          {dirty && (
            <span className="text-[11px] text-amber-300/80">Unsaved changes</span>
          )}
          <Button
            size="sm"
            onClick={advancedOpen ? onSaveAdvanced : onSavePicker}
            disabled={!dirty || save.isPending}
          >
            {save.isPending ? "Saving…" : "Save scopes"}
          </Button>
        </div>
      </footer>
    </div>
  );
}

type ScopeGroupProps = {
  title: string;
  subtitle: string;
  entries: readonly ScopeCatalogEntry[];
  checked: Set<string>;
  disabled?: boolean;
  onToggle: (name: string) => void;
};

function ScopeGroup({
  title,
  subtitle,
  entries,
  checked,
  disabled,
  onToggle,
}: ScopeGroupProps) {
  return (
    <div className="space-y-2">
      <div className="flex items-baseline justify-between">
        <h3 className="text-[11px] font-medium uppercase tracking-widest text-muted-foreground/70">
          {title}
        </h3>
        <span className="text-[11px] text-muted-foreground/50">{subtitle}</span>
      </div>
      <div className="space-y-1.5">
        {entries.map((entry) => {
          const isChecked = disabled || checked.has(entry.name);
          return (
            <label
              key={entry.name}
              className={cn(
                "flex cursor-pointer items-start gap-3 rounded-md border border-white/[0.04] bg-white/[0.02] px-3 py-2.5 transition-colors",
                disabled
                  ? "cursor-not-allowed opacity-90"
                  : "hover:border-white/[0.08] hover:bg-white/[0.04]",
              )}
            >
              <input
                type="checkbox"
                checked={isChecked}
                disabled={disabled}
                onChange={() => !disabled && onToggle(entry.name)}
                className="mt-0.5 h-3.5 w-3.5 shrink-0 rounded border border-white/20 bg-white/[0.04] accent-purple-400"
              />
              <div className="min-w-0 flex-1 space-y-0.5">
                <div className="flex items-center gap-2">
                  <span className="text-sm text-foreground">{entry.label}</span>
                  <code className="rounded bg-white/[0.04] px-1.5 py-0.5 font-mono text-[10px] text-muted-foreground">
                    {entry.name}
                  </code>
                  {disabled && (
                    <Lock
                      className="h-3 w-3 text-muted-foreground/50"
                      aria-label="required, cannot be disabled"
                    />
                  )}
                </div>
                <div className="text-xs text-muted-foreground">
                  {entry.description}
                </div>
              </div>
            </label>
          );
        })}
      </div>
    </div>
  );
}

function parseScopesEqual(a: Set<string>, b: Set<string>): boolean {
  if (a.size !== b.size) return false;
  for (const s of a) if (!b.has(s)) return false;
  return true;
}
