import * as React from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { BarChart3, KeyRound, ShieldCheck, TerminalSquare } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { Switch } from "@/components/ui/switch";
import { SettingField } from "@/components/settings/SettingField";
import { reportingAdminApi, settingsApi, type SettingEntry } from "@/lib/api";

const SETTINGS_QUERY_KEY = ["settings", "list", "Reporting"] as const;
const STATUS_QUERY_KEY = ["settings", "reporting", "status"] as const;

const KEY_IP_ALLOW_LIST = "Reporting.IpAllowList";
const KEY_MAX_LIST_ITEMS = "Reporting.MaxListItems";

function findEntry(entries: SettingEntry[] | undefined, key: string) {
  return entries?.find((e) => e.key === key);
}

export function ReportingSettingsPage() {
  const query = useQuery({
    queryKey: SETTINGS_QUERY_KEY,
    queryFn: () => settingsApi.list("Reporting"),
  });

  const ipAllowListEntry = findEntry(query.data, KEY_IP_ALLOW_LIST);
  const maxListItemsEntry = findEntry(query.data, KEY_MAX_LIST_ITEMS);

  return (
    <div className="flex flex-col gap-6">
      <header className="flex items-start justify-between gap-4">
        <div className="space-y-2">
          <div className="mb-2 text-primary">
            <BarChart3 className="h-6 w-6" />
          </div>
          <h1 className="text-display-md font-semibold text-foreground">Reporting API</h1>
          <p className="max-w-xl text-sm text-muted-foreground">
            A read-only endpoint external tooling can poll for ticket
            statistics over a period — tickets opened, tickets closed and the
            current open backlog, each as a count plus ticket number + subject
            list. Gated by a pre-shared API key and an optional IP allow-list;
            invisible (404) until both the switch below is on and a key is
            configured.
          </p>
        </div>
        <Badge className="border border-glass bg-glass text-xs font-normal text-muted-foreground">
          Admin only
        </Badge>
      </header>

      <AccessSection />

      <section className="glass-card p-6">
        <SectionHeader
          icon={<ShieldCheck className="h-5 w-5" />}
          title="Restrictions"
          description="Optional IP allow-list and the per-section cap on returned ticket rows. Counts always reflect full totals."
        />
        {query.isLoading ? (
          <div className="space-y-3">
            <Skeleton className="h-10 w-full" />
            <Skeleton className="h-10 w-full" />
          </div>
        ) : (
          <div>
            {ipAllowListEntry ? (
              <SettingField
                entry={ipAllowListEntry}
                queryKey={SETTINGS_QUERY_KEY}
                label="IP allow-list"
                hint="Comma-separated plain IPs and/or CIDR ranges (IPv4 and IPv6), e.g. 203.0.113.10, 198.51.100.0/24. Empty = no IP restriction; the API key alone gates access. Callers outside the list get a 404."
              />
            ) : (
              <MissingEntry keyName={KEY_IP_ALLOW_LIST} />
            )}
            {maxListItemsEntry ? (
              <SettingField
                entry={maxListItemsEntry}
                queryKey={SETTINGS_QUERY_KEY}
                label="Max ticket rows per section"
                hint="Maximum ticket rows (number + subject) returned per section (opened / closed / open) in one response. Longer lists are paged via the offset query parameters. 0 = counts only."
              />
            ) : (
              <MissingEntry keyName={KEY_MAX_LIST_ITEMS} />
            )}
          </div>
        )}
      </section>

      <UsageSection />
    </div>
  );
}

// ---- Access section (master switch + API key) --------------------------

function generateKey(): string {
  const bytes = new Uint8Array(30);
  crypto.getRandomValues(bytes);
  const chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
  return Array.from(bytes, (b) => chars[b % chars.length]).join("");
}

function AccessSection() {
  const qc = useQueryClient();
  const [showInput, setShowInput] = React.useState(false);
  const [draft, setDraft] = React.useState("");

  const status = useQuery({
    queryKey: STATUS_QUERY_KEY,
    queryFn: () => reportingAdminApi.status(),
  });

  const setEnabled = useMutation({
    mutationFn: (enabled: boolean) => reportingAdminApi.setEnabled(enabled),
    onSuccess: (_, enabled) => {
      qc.invalidateQueries({ queryKey: STATUS_QUERY_KEY });
      toast.success(enabled ? "Reporting API enabled" : "Reporting API disabled");
    },
    onError: (err) => toast.error(err instanceof Error ? err.message : "Failed to update"),
  });

  const setKey = useMutation({
    mutationFn: (value: string) => reportingAdminApi.setKey(value),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: STATUS_QUERY_KEY });
      toast.success("API key saved");
      setDraft("");
      setShowInput(false);
    },
    onError: (err) => toast.error(err instanceof Error ? err.message : "Failed to save key"),
  });

  const deleteKey = useMutation({
    mutationFn: () => reportingAdminApi.deleteKey(),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: STATUS_QUERY_KEY });
      toast.success("API key cleared");
    },
    onError: (err) => toast.error(err instanceof Error ? err.message : "Failed to clear key"),
  });

  const configured = status.data?.keyConfigured ?? false;
  const showDraftInput = showInput || !configured;

  return (
    <section className="glass-card p-6">
      <SectionHeader
        icon={<KeyRound className="h-5 w-5" />}
        title="Access"
        description="Master switch and the pre-shared API key external callers send on every request. The endpoint is only live when the switch is on AND a key is configured."
      />

      {status.isLoading ? (
        <div className="space-y-3">
          <Skeleton className="h-10 w-full" />
          <Skeleton className="h-10 w-full" />
        </div>
      ) : (
        <div className="space-y-0">
          <div className="flex items-start justify-between gap-4 border-b border-glass py-3">
            <div className="min-w-0 flex-1">
              <p className="text-sm font-medium text-foreground">Enable Reporting API</p>
              <p className="mt-0.5 text-xs text-muted-foreground">
                When off, the endpoint answers 404 to everyone regardless of the key.
              </p>
            </div>
            <div className="shrink-0">
              <Switch
                checked={status.data?.enabled ?? false}
                disabled={setEnabled.isPending}
                onCheckedChange={(v) => setEnabled.mutate(v)}
              />
            </div>
          </div>

          <div className="py-3">
            <div className="mb-3 flex flex-wrap items-center gap-3">
              <p className="text-sm font-medium text-foreground">API key</p>
              {configured && !showInput && (
                <>
                  <Badge className="border border-emerald-400/20 bg-emerald-400/10 text-[10px] font-normal text-emerald-200">
                    Key configured
                  </Badge>
                  <Button
                    size="sm"
                    variant="ghost"
                    className="h-7 px-2 text-xs"
                    onClick={() => { setShowInput(true); setDraft(""); }}
                  >
                    Replace key
                  </Button>
                  <Button
                    size="sm"
                    variant="ghost"
                    className="h-7 px-2 text-xs text-destructive hover:text-destructive"
                    disabled={deleteKey.isPending}
                    onClick={() => deleteKey.mutate()}
                  >
                    Clear key
                  </Button>
                </>
              )}
            </div>

            {showDraftInput && (
              <div className="space-y-2">
                <div className="flex items-center gap-2">
                  <Input
                    type="text"
                    value={draft}
                    onChange={(e) => setDraft(e.target.value)}
                    placeholder="Paste or generate a key…"
                    className="h-9 flex-1 font-mono text-sm"
                    disabled={setKey.isPending}
                  />
                  <Button
                    size="sm"
                    variant="ghost"
                    className="h-9 shrink-0 px-3"
                    onClick={() => setDraft(generateKey())}
                    disabled={setKey.isPending}
                  >
                    Generate
                  </Button>
                  <Button
                    size="sm"
                    className="h-9 shrink-0 px-3"
                    disabled={draft.length < 24 || setKey.isPending}
                    onClick={() => setKey.mutate(draft)}
                  >
                    Save key
                  </Button>
                  {showInput && configured && (
                    <Button
                      size="sm"
                      variant="ghost"
                      className="h-9 shrink-0 px-2 text-xs text-muted-foreground"
                      onClick={() => { setShowInput(false); setDraft(""); }}
                      disabled={setKey.isPending}
                    >
                      Cancel
                    </Button>
                  )}
                </div>
                <p className="text-xs text-muted-foreground">
                  24–256 characters. Store it safely — it is shown only once here and never displayed again.
                </p>
              </div>
            )}
          </div>
        </div>
      )}
    </section>
  );
}

// ---- Usage section (endpoint reference) --------------------------------

function UsageSection() {
  return (
    <section className="glass-card p-6">
      <SectionHeader
        icon={<TerminalSquare className="h-5 w-5" />}
        title="Usage"
        description="How an external caller queries the endpoint. All timestamps are UTC; a date without an offset is interpreted as UTC."
      />
      <div className="space-y-3 text-sm text-muted-foreground">
        <pre className="overflow-x-auto rounded-lg border border-glass bg-glass p-4 font-mono text-xs leading-relaxed text-foreground/90">
{`GET /api/reporting/tickets?from=2026-08-01&to=2026-09-01
X-Reporting-Api-Key: <your key>`}
        </pre>
        <ul className="list-disc space-y-1 pl-5 text-xs">
          <li>
            <code className="font-mono text-foreground/80">from</code> /{" "}
            <code className="font-mono text-foreground/80">to</code> — required,
            ISO 8601 date or date-time; the period is interpreted as{" "}
            <code className="font-mono text-foreground/80">[from, to)</code>.
          </li>
          <li>
            The response contains three sections — <code className="font-mono text-foreground/80">opened</code>{" "}
            (created in the period), <code className="font-mono text-foreground/80">closed</code>{" "}
            (resolved or closed in the period) and{" "}
            <code className="font-mono text-foreground/80">openNow</code> (current
            open backlog, period-independent) — each with a total count and a
            capped list of ticket number + subject.
          </li>
          <li>
            When a section is truncated, page through it with{" "}
            <code className="font-mono text-foreground/80">openedOffset</code>,{" "}
            <code className="font-mono text-foreground/80">closedOffset</code> and{" "}
            <code className="font-mono text-foreground/80">openOffset</code>.
          </li>
          <li>Every request is audited; requests are rate-limited per IP.</li>
        </ul>
      </div>
    </section>
  );
}

// ---- shared bits ------------------------------------------------------

function SectionHeader({
  icon,
  title,
  description,
}: {
  icon: React.ReactNode;
  title: string;
  description: string;
}) {
  return (
    <div className="mb-4 flex items-center gap-3">
      <div className="rounded-md bg-glass p-2 text-primary">{icon}</div>
      <div>
        <h2 className="text-base font-semibold text-foreground">{title}</h2>
        <p className="text-xs text-muted-foreground">{description}</p>
      </div>
    </div>
  );
}

function MissingEntry({ keyName }: { keyName: string }) {
  return (
    <p className="text-xs text-amber-200/70">
      Setting <code className="font-mono">{keyName}</code> not seeded yet —
      restart the API once to run the settings seeder.
    </p>
  );
}
