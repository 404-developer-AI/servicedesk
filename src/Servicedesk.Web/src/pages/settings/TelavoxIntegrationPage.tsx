import { useEffect, useMemo, useState } from "react";
import { Link } from "@tanstack/react-router";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import {
  AlertCircle,
  ArrowLeft,
  CheckCircle2,
  History,
  KeyRound,
  Link as LinkIcon,
  Phone,
  RefreshCw,
  Trash2,
  Unlink,
} from "lucide-react";
import {
  ApiError,
  apiErrorMessage,
  settingsApi,
  telavoxAdminApi,
  type SettingEntry,
  type TelavoxAgentLink,
  type TelavoxConnectionState,
  type TelavoxCustomer,
  type TelavoxExtension,
} from "@/lib/api";
import { userApi, type AgentUser } from "@/lib/ticket-api";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { SettingField } from "@/components/settings/SettingField";
import { IntegrationAuditLog } from "@/components/integrations/IntegrationAuditLog";
import { cn } from "@/lib/utils";

const STATUS_QK = ["integrations", "telavox", "status"] as const;
const SECRET_QK = ["integrations", "telavox", "secret"] as const;
const EXTENSIONS_QK = ["integrations", "telavox", "extensions"] as const;
const AGENTS_QK = ["integrations", "telavox", "agents"] as const;
const USERS_QK = ["users", "agents-and-admins"] as const;
const SETTINGS_QK = ["settings", "list", "Telavox"] as const;

const STATE_LABEL: Record<
  TelavoxConnectionState,
  { tone: string; text: string; dot: string }
> = {
  Disabled: {
    tone: "border-glass-strong bg-glass-strong text-muted-foreground",
    text: "Disabled",
    dot: "bg-glass-strong",
  },
  NotConfigured: {
    tone: "border-amber-400/30 bg-amber-500/[0.08] text-amber-200",
    text: "Not configured",
    dot: "bg-amber-400",
  },
  NoCustomerSelected: {
    tone: "border-amber-400/30 bg-amber-500/[0.08] text-amber-200",
    text: "Pick a customer",
    dot: "bg-amber-400",
  },
  Ready: {
    tone: "border-sky-400/30 bg-sky-500/10 text-sky-300",
    text: "Ready — link an agent",
    dot: "bg-sky-400",
  },
  Active: {
    tone: "border-emerald-400/30 bg-emerald-500/10 text-emerald-300",
    text: "Active",
    dot: "bg-emerald-400",
  },
};

function findEntry(entries: SettingEntry[] | undefined, key: string) {
  return entries?.find((e) => e.key === key);
}

export function TelavoxIntegrationPage() {
  const qc = useQueryClient();

  const status = useQuery({ queryKey: STATUS_QK, queryFn: telavoxAdminApi.status });
  const secret = useQuery({ queryKey: SECRET_QK, queryFn: telavoxAdminApi.secretStatus });
  const settingsList = useQuery({
    queryKey: SETTINGS_QK,
    queryFn: () => settingsApi.list("Telavox"),
  });

  const hasToken = secret.data?.configured ?? false;
  const customerPinned = (status.data?.partnerCustomerId ?? "").length > 0;

  const extensions = useQuery({
    queryKey: EXTENSIONS_QK,
    queryFn: telavoxAdminApi.listExtensions,
    enabled: hasToken && customerPinned,
  });
  const agentLinks = useQuery({
    queryKey: AGENTS_QK,
    queryFn: telavoxAdminApi.listAgentLinks,
    enabled: hasToken,
  });
  const users = useQuery({
    queryKey: USERS_QK,
    queryFn: () => userApi.listAgents(),
    enabled: hasToken && customerPinned,
  });

  const [tokenDraft, setTokenDraft] = useState("");
  const [customerOptions, setCustomerOptions] = useState<TelavoxCustomer[] | null>(null);
  const [customerPick, setCustomerPick] = useState("");

  // Reset the picker whenever the pinned id changes — keeps the dropdown
  // value in sync with what's actually saved.
  useEffect(() => {
    setCustomerPick(status.data?.partnerCustomerId ?? "");
  }, [status.data?.partnerCustomerId]);

  const saveSecret = useMutation({
    mutationFn: () => telavoxAdminApi.setSecret(tokenDraft),
    onSuccess: () => {
      toast.success("Partner token saved");
      setTokenDraft("");
      qc.invalidateQueries({ queryKey: SECRET_QK });
      qc.invalidateQueries({ queryKey: STATUS_QK });
    },
    onError: (err) =>
      toast.error(
        err instanceof ApiError ? `Save failed (${err.status})` : "Save failed",
      ),
  });

  const deleteSecret = useMutation({
    mutationFn: () => telavoxAdminApi.deleteSecret(),
    onSuccess: () => {
      toast.success("Partner token cleared");
      setCustomerOptions(null);
      qc.invalidateQueries({ queryKey: SECRET_QK });
      qc.invalidateQueries({ queryKey: STATUS_QK });
      qc.invalidateQueries({ queryKey: EXTENSIONS_QK });
      qc.invalidateQueries({ queryKey: AGENTS_QK });
    },
  });

  const test = useMutation({
    mutationFn: () => telavoxAdminApi.testConnection(),
    onSuccess: (r) => {
      setCustomerOptions(r.customers);
      if (r.customers.length === 0) {
        toast.warning("Token works but no customers were returned");
      } else {
        toast.success(`Token works — ${r.customers.length} customer(s) found`);
      }
    },
    onError: (err) => {
      // The backend wraps PAPI errors as 502 with an envelope carrying the
      // real upstream message + error code. Surface that verbatim so the
      // admin sees "invalid_credentials" or "no route to host" instead of
      // a generic "Test failed (502)" that gives no diagnostic value.
      const upstream = apiErrorMessage(err);
      const status = err instanceof ApiError ? err.status : null;
      toast.error(
        upstream
          ? `Test failed: ${upstream}`
          : status
            ? `Test failed (HTTP ${status})`
            : "Test failed — check the token and partner URL",
      );
    },
  });

  const pinCustomer = useMutation({
    mutationFn: (id: string) => telavoxAdminApi.setCustomerId(id),
    onSuccess: () => {
      toast.success("Customer pinned");
      qc.invalidateQueries({ queryKey: STATUS_QK });
      qc.invalidateQueries({ queryKey: EXTENSIONS_QK });
      // Switching customers makes the previously-listed extensions stale
      // — and so are any agent links that pointed at them. Refetching the
      // mapping list makes the admin's stale-row situation visible
      // immediately instead of on the next page mount.
      qc.invalidateQueries({ queryKey: AGENTS_QK });
    },
  });

  const clearCustomer = useMutation({
    mutationFn: () => telavoxAdminApi.clearCustomerId(),
    onSuccess: () => {
      toast.success("Customer cleared");
      setCustomerPick("");
      qc.invalidateQueries({ queryKey: STATUS_QK });
      qc.invalidateQueries({ queryKey: EXTENSIONS_QK });
      qc.invalidateQueries({ queryKey: AGENTS_QK });
    },
  });

  const state = status.data?.state ?? "Disabled";
  const stateMeta = STATE_LABEL[state];

  return (
    <div className="flex flex-col gap-6">
      <header className="flex items-start justify-between gap-4">
        <div className="space-y-2">
          <Link
            to="/settings/integrations"
            className="inline-flex items-center gap-1 text-xs text-muted-foreground/70 transition-colors hover:text-foreground"
          >
            <ArrowLeft className="h-3 w-3" /> Integrations
          </Link>
          <div className="mt-2 mb-2 text-primary">
            <Phone className="h-6 w-6" />
          </div>
          <h1 className="text-display-md font-semibold text-foreground">Telavox</h1>
          <p className="max-w-xl text-sm text-muted-foreground">
            Surfaces a call popup on every inbound call for mapped agents. Uses
            polling against the Telavox CAPI — no webhooks. One partner token
            per install plus one extension per agent.
          </p>
        </div>
        <div className="flex items-center gap-2">
          <span
            className={cn(
              "inline-flex items-center gap-2 rounded-full border px-3 py-1 text-xs font-medium",
              stateMeta.tone,
            )}
          >
            <span className={cn("h-1.5 w-1.5 rounded-full", stateMeta.dot)} />
            {stateMeta.text}
          </span>
          <Badge className="border border-glass bg-glass text-xs font-normal text-muted-foreground">
            Admin only
          </Badge>
        </div>
      </header>

      {/* ---- Configuration ----------------------------------------- */}
      <section className="space-y-4 rounded-xl border border-glass-strong bg-glass p-5">
        <div className="flex items-center gap-2 text-sm font-medium text-foreground">
          <KeyRound className="h-4 w-4 text-muted-foreground" />
          Partner token
        </div>
        <p className="text-xs text-muted-foreground/70">
          Long-lived bearer issued by Telavox. Stored encrypted via the
          DataProtection key-ring; never echoed back.
        </p>
        <div className="flex flex-wrap gap-2">
          <Input
            type="password"
            autoComplete="new-password"
            placeholder={hasToken ? "Replace token…" : "Paste partner token…"}
            value={tokenDraft}
            onChange={(e) => setTokenDraft(e.target.value)}
            className="h-9 w-80 bg-glass font-mono text-sm"
          />
          <Button
            size="sm"
            className="h-9"
            disabled={tokenDraft.trim().length === 0 || saveSecret.isPending}
            onClick={() => saveSecret.mutate()}
          >
            {hasToken ? "Replace" : "Save"}
          </Button>
          {hasToken && (
            <Button
              size="sm"
              variant="ghost"
              className="h-9 text-muted-foreground hover:text-foreground"
              disabled={deleteSecret.isPending}
              onClick={() => deleteSecret.mutate()}
            >
              <Trash2 className="mr-1.5 h-3.5 w-3.5" />
              Clear
            </Button>
          )}
          <Button
            size="sm"
            variant="ghost"
            className="h-9"
            disabled={!hasToken || test.isPending}
            onClick={() => test.mutate()}
          >
            <RefreshCw className={cn("mr-1.5 h-3.5 w-3.5", test.isPending && "animate-spin")} />
            Test connection
          </Button>
          {hasToken ? (
            <span className="inline-flex items-center gap-1 text-xs text-emerald-300">
              <CheckCircle2 className="h-3 w-3" /> Token saved
            </span>
          ) : (
            <span className="inline-flex items-center gap-1 text-xs text-muted-foreground/60">
              <AlertCircle className="h-3 w-3" /> No token configured
            </span>
          )}
        </div>
      </section>

      {/* ---- Customer pin ------------------------------------------ */}
      <section className="space-y-4 rounded-xl border border-glass-strong bg-glass p-5">
        <div className="flex items-center gap-2 text-sm font-medium text-foreground">
          <LinkIcon className="h-4 w-4 text-muted-foreground" />
          Customer
        </div>
        <p className="text-xs text-muted-foreground/70">
          One Telavox customer-id per install. Pick from the dropdown after a
          successful test-connection; the choice gates the extensions list
          below.
        </p>
        <div className="flex flex-wrap items-center gap-2">
          <Select
            value={customerPick}
            onValueChange={setCustomerPick}
            disabled={!hasToken || customerOptions === null}
          >
            <SelectTrigger className="h-9 w-80 bg-glass text-sm">
              <SelectValue
                placeholder={
                  customerOptions === null
                    ? "Run Test connection to load customers…"
                    : "Select a customer…"
                }
              />
            </SelectTrigger>
            <SelectContent>
              {(customerOptions ?? []).map((c) => (
                <SelectItem key={c.id} value={c.id}>
                  <span className="font-medium">{c.name}</span>
                  <span className="ml-2 text-xs text-muted-foreground">{c.id}</span>
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
          <Button
            size="sm"
            className="h-9"
            disabled={
              !customerPick ||
              customerPick === status.data?.partnerCustomerId ||
              pinCustomer.isPending
            }
            onClick={() => pinCustomer.mutate(customerPick)}
          >
            Pin
          </Button>
          {customerPinned && (
            <>
              <span className="inline-flex items-center gap-1 text-xs text-emerald-300">
                <CheckCircle2 className="h-3 w-3" /> {status.data?.partnerCustomerId}
              </span>
              <Button
                size="sm"
                variant="ghost"
                className="h-9 text-muted-foreground hover:text-foreground"
                disabled={clearCustomer.isPending}
                onClick={() => clearCustomer.mutate()}
              >
                <Trash2 className="mr-1.5 h-3.5 w-3.5" />
                Clear
              </Button>
            </>
          )}
        </div>
      </section>

      {/* ---- Agent mapping ----------------------------------------- */}
      <AgentMappingSection
        users={users.data}
        usersLoading={users.isLoading}
        extensions={extensions.data?.items ?? []}
        extensionsLoading={extensions.isLoading}
        links={agentLinks.data?.items ?? []}
        linksLoading={agentLinks.isLoading}
        hasToken={hasToken}
        customerPinned={customerPinned}
      />

      {/* ---- Tunables ---------------------------------------------- */}
      <section className="space-y-1 rounded-xl border border-glass-strong bg-glass p-5">
        <div className="mb-3 text-sm font-medium text-foreground">Behaviour</div>
        {settingsList.isLoading ? (
          <div className="space-y-2">
            <Skeleton className="h-10 w-full bg-glass" />
            <Skeleton className="h-10 w-full bg-glass" />
            <Skeleton className="h-10 w-full bg-glass" />
          </div>
        ) : (
          <>
            <FieldOrSkeleton
              entry={findEntry(settingsList.data, "Telavox.Enabled")}
              queryKey={SETTINGS_QK}
              label="Enabled"
            />
            <FieldOrSkeleton
              entry={findEntry(settingsList.data, "Telavox.PopupTriggerMode")}
              queryKey={SETTINGS_QK}
              label="Popup trigger"
              hint="When the popup fires: 'answered' (default) waits for the agent to pick up; 'ringing' shows on the ring; 'either' fires on both edges."
            />
            <FieldOrSkeleton
              entry={findEntry(settingsList.data, "Telavox.PollIntervalSeconds")}
              queryKey={SETTINGS_QK}
              label="Idle poll interval (seconds)"
              hint="How often each linked agent is checked when no call is ringing. Default 10. Range 2–60. Lower = faster ring detection but more Telavox-side load on quiet installs."
            />
            <FieldOrSkeleton
              entry={findEntry(settingsList.data, "Telavox.RingingPollIntervalSeconds")}
              queryKey={SETTINGS_QK}
              label="Ringing poll interval (seconds)"
              hint="Cadence while any linked agent is ringing — used until the call is picked up or dropped. Default 1 so the answered-edge that fires the popup lands within a second of the actual pickup. Range 1–10; should be ≤ idle interval."
            />
            <FieldOrSkeleton
              entry={findEntry(settingsList.data, "Telavox.DefaultCountryCode")}
              queryKey={SETTINGS_QK}
              label="Default country (ISO)"
              hint="ISO-3166 alpha-2 used to canonicalise raw CAPI phone numbers to E.164 before contact-lookup."
            />
            <FieldOrSkeleton
              entry={findEntry(settingsList.data, "Telavox.PollWindow.Start")}
              queryKey={SETTINGS_QK}
              label="Window start (HH:mm)"
            />
            <FieldOrSkeleton
              entry={findEntry(settingsList.data, "Telavox.PollWindow.End")}
              queryKey={SETTINGS_QK}
              label="Window end (HH:mm)"
            />
            <FieldOrSkeleton
              entry={findEntry(settingsList.data, "Telavox.PollWindow.Days")}
              queryKey={SETTINGS_QK}
              label="Active days (Mon..Sun)"
              hint="Comma-separated booleans, Monday first — for example 'true,true,true,true,true,false,false' is Mon-Fri only."
            />
          </>
        )}
      </section>

      {/* ---- Integration audit log -------------------------------- */}
      <section className="space-y-3 rounded-xl border border-glass-strong bg-glass p-5">
        <div className="flex items-center gap-2 text-sm font-medium text-foreground">
          <History className="h-4 w-4 text-muted-foreground" />
          Audit log
        </div>
        <p className="text-xs text-muted-foreground/70">
          Every outbound Telavox PAPI / CAPI call writes a row here with
          latency, HTTP status and upstream error code. The first place to
          look when a "Test connection" returns 502 or the popup stops
          firing for a mapped agent.
        </p>
        <IntegrationAuditLog integration="telavox" />
      </section>
    </div>
  );
}

function FieldOrSkeleton({
  entry,
  queryKey,
  label,
  hint,
}: {
  entry: SettingEntry | undefined;
  queryKey: readonly unknown[];
  label: string;
  hint?: string;
}) {
  if (!entry) {
    return (
      <div className="flex items-center justify-between gap-4 py-3 text-xs text-muted-foreground/60">
        <span>{label}</span>
        <span className="italic">missing</span>
      </div>
    );
  }
  return <SettingField entry={entry} queryKey={queryKey} label={label} hint={hint} />;
}

function AgentMappingSection({
  users,
  usersLoading,
  extensions,
  extensionsLoading,
  links,
  linksLoading,
  hasToken,
  customerPinned,
}: {
  users: AgentUser[] | undefined;
  usersLoading: boolean;
  extensions: TelavoxExtension[];
  extensionsLoading: boolean;
  links: TelavoxAgentLink[];
  linksLoading: boolean;
  hasToken: boolean;
  customerPinned: boolean;
}) {
  const qc = useQueryClient();

  const linkByUser = useMemo(() => {
    const m = new Map<string, TelavoxAgentLink>();
    for (const l of links) m.set(l.userId, l);
    return m;
  }, [links]);

  const eligibleUsers = useMemo(
    () =>
      (users ?? []).filter(
        (u) => u.roleName === "Agent" || u.roleName === "Admin",
      ),
    [users],
  );

  const provision = useMutation({
    mutationFn: ({ userId, extension }: { userId: string; extension: string }) =>
      telavoxAdminApi.provisionAgent(userId, extension),
    onSuccess: () => {
      toast.success("Agent linked");
      qc.invalidateQueries({ queryKey: AGENTS_QK });
      qc.invalidateQueries({ queryKey: STATUS_QK });
    },
    onError: (err) => {
      // Backend distinguishes 400 (config/validation), 502 (upstream
      // PAPI). Surface the server's structured `message` so the admin
      // sees e.g. "User is not active" or "extension must be ..." instead
      // of a generic "Link failed (400)".
      const upstream = apiErrorMessage(err);
      const status = err instanceof ApiError ? err.status : null;
      toast.error(
        upstream
          ? `Link failed: ${upstream}`
          : status
            ? `Link failed (HTTP ${status})`
            : "Link failed",
      );
    },
  });

  const provisionManual = useMutation({
    mutationFn: ({
      userId,
      extension,
      capiToken,
    }: {
      userId: string;
      extension: string;
      capiToken: string;
    }) => telavoxAdminApi.provisionAgentManual(userId, extension, capiToken),
    onSuccess: () => {
      toast.success("Agent linked (manual token)");
      qc.invalidateQueries({ queryKey: AGENTS_QK });
      qc.invalidateQueries({ queryKey: STATUS_QK });
    },
    onError: (err) => {
      const upstream = apiErrorMessage(err);
      const status = err instanceof ApiError ? err.status : null;
      toast.error(
        upstream
          ? `Manual link failed: ${upstream}`
          : status
            ? `Manual link failed (HTTP ${status})`
            : "Manual link failed",
      );
    },
  });

  const revoke = useMutation({
    mutationFn: (userId: string) => telavoxAdminApi.revokeAgent(userId),
    onSuccess: () => {
      toast.success("Agent unlinked");
      qc.invalidateQueries({ queryKey: AGENTS_QK });
      qc.invalidateQueries({ queryKey: STATUS_QK });
    },
  });

  return (
    <section className="space-y-3 rounded-xl border border-glass-strong bg-glass p-5">
      <div className="flex items-center justify-between">
        <div className="flex items-center gap-2 text-sm font-medium text-foreground">
          <LinkIcon className="h-4 w-4 text-muted-foreground" />
          Agent mapping
        </div>
        <span className="text-xs text-muted-foreground/60">
          {links.length} of {eligibleUsers.length} mapped
        </span>
      </div>
      {!hasToken || !customerPinned ? (
        <p className="text-xs text-muted-foreground/70">
          Save a partner token and pin a customer first — the extensions list
          below depends on both.
        </p>
      ) : usersLoading || linksLoading ? (
        <div className="space-y-2">
          <Skeleton className="h-10 w-full bg-glass" />
          <Skeleton className="h-10 w-full bg-glass" />
        </div>
      ) : eligibleUsers.length === 0 ? (
        <p className="text-xs text-muted-foreground/70">
          No agents or admins exist yet. Add them under Settings → Users.
        </p>
      ) : (
        <div className="divide-y divide-glass">
          {eligibleUsers.map((u) => {
            const link = linkByUser.get(u.id);
            return (
              <AgentMappingRow
                key={u.id}
                user={u}
                link={link}
                extensions={extensions}
                extensionsLoading={extensionsLoading}
                onLink={(extension) =>
                  provision.mutate({ userId: u.id, extension })
                }
                onLinkManual={(extension, capiToken) =>
                  provisionManual.mutate({ userId: u.id, extension, capiToken })
                }
                onUnlink={() => revoke.mutate(u.id)}
                busy={
                  provision.isPending ||
                  provisionManual.isPending ||
                  revoke.isPending
                }
              />
            );
          })}
        </div>
      )}
    </section>
  );
}

function AgentMappingRow({
  user,
  link,
  extensions,
  extensionsLoading,
  onLink,
  onLinkManual,
  onUnlink,
  busy,
}: {
  user: AgentUser;
  link: TelavoxAgentLink | undefined;
  extensions: TelavoxExtension[];
  extensionsLoading: boolean;
  onLink: (extension: string) => void;
  onLinkManual: (extension: string, capiToken: string) => void;
  onUnlink: () => void;
  busy: boolean;
}) {
  const [pick, setPick] = useState<string>(link?.telavoxExtension ?? "");
  const [manualOpen, setManualOpen] = useState(false);

  useEffect(() => {
    if (link?.telavoxExtension) setPick(link.telavoxExtension);
  }, [link?.telavoxExtension]);

  const hasError = !!link?.lastPollError;
  const isManualLink = link?.telavoxUserId === "manual";

  return (
    <div className="flex items-center justify-between gap-3 py-3">
      <div className="min-w-0 flex-1">
        <p className="truncate text-sm font-medium text-foreground">{user.email}</p>
        <p className="text-[10px] uppercase tracking-wider text-muted-foreground/50">
          {user.roleName}
          {isManualLink ? (
            <span className="ml-1.5 inline-flex items-center rounded border border-amber-400/30 bg-amber-500/[0.08] px-1.5 py-0 text-[9px] tracking-wider text-amber-200">
              manual
            </span>
          ) : null}
          {link && hasError ? (
            <>
              {" · "}
              <span className="text-rose-300">
                Last poll: {link.lastPollError}
              </span>
            </>
          ) : link?.lastPollUtc ? (
            <>
              {" · "}
              Last poll OK
            </>
          ) : null}
        </p>
      </div>
      <div className="flex items-center gap-2">
        <Select value={pick} onValueChange={setPick} disabled={busy || !!link}>
          <SelectTrigger className="h-8 w-52 bg-glass text-xs">
            <SelectValue
              placeholder={
                extensionsLoading
                  ? "Loading extensions…"
                  : extensions.length === 0
                    ? "No extensions available"
                    : "Select extension…"
              }
            />
          </SelectTrigger>
          <SelectContent>
            {extensions.map((e) => (
              // value = extension KEY (e.id) — CAPI's
              // /v1/extensions/{extension}/calls takes the key as path-
              // param. The dialable number is just for display.
              <SelectItem key={e.id} value={e.id}>
                <span className="font-mono">{e.number || e.id}</span>
                {e.name ? (
                  <span className="ml-2 text-xs text-muted-foreground">{e.name}</span>
                ) : null}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
        {link ? (
          <Button
            size="sm"
            variant="ghost"
            className="h-8 text-muted-foreground hover:text-rose-300"
            disabled={busy}
            onClick={onUnlink}
          >
            <Unlink className="mr-1 h-3 w-3" /> Unlink
          </Button>
        ) : (
          <>
            <Button
              size="sm"
              className="h-8"
              disabled={busy || !pick}
              onClick={() => onLink(pick)}
            >
              Link
            </Button>
            <Button
              size="sm"
              variant="ghost"
              className="h-8 text-muted-foreground hover:text-foreground"
              disabled={busy || !pick}
              onClick={() => setManualOpen(true)}
              title="Use a CAPI token minted from the Telavox webapp instead of the auto-provision flow."
            >
              Manual…
            </Button>
          </>
        )}
      </div>

      <ManualLinkDialog
        open={manualOpen}
        onOpenChange={setManualOpen}
        extension={pick}
        extensionLabel={
          extensions.find((e) => e.id === pick)?.number ?? pick
        }
        userEmail={user.email}
        onConfirm={(token) => {
          onLinkManual(pick, token);
          setManualOpen(false);
        }}
        busy={busy}
      />
    </div>
  );
}

function ManualLinkDialog({
  open,
  onOpenChange,
  extension,
  extensionLabel,
  userEmail,
  onConfirm,
  busy,
}: {
  open: boolean;
  onOpenChange: (v: boolean) => void;
  extension: string;
  extensionLabel: string;
  userEmail: string;
  onConfirm: (token: string) => void;
  busy: boolean;
}) {
  const [token, setToken] = useState("");

  // Clear the paste-buffer every time the dialog opens so a previous
  // attempt's token doesn't linger in component state when the admin
  // opens the dialog for a different agent.
  useEffect(() => {
    if (open) setToken("");
  }, [open]);

  const canSubmit = token.trim().length > 0 && extension.length > 0 && !busy;

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-w-lg">
        <DialogHeader>
          <DialogTitle>Link with manual CAPI token</DialogTitle>
          <DialogDescription>
            Use this when the auto-provision flow returns a Telavox error
            (typically because PAPI api-user creation is not enabled on
            your account). Paste a CAPI bearer token minted from the
            Telavox webapp by the agent themselves — the polling worker
            will use it verbatim to read the extension's ongoing calls.
          </DialogDescription>
        </DialogHeader>

        <div className="space-y-3 text-xs">
          <div className="space-y-1">
            <p className="text-muted-foreground/70">Agent</p>
            <p className="font-mono text-foreground">{userEmail}</p>
          </div>
          <div className="space-y-1">
            <p className="text-muted-foreground/70">Extension</p>
            <p className="font-mono text-foreground">
              {extensionLabel || extension || "—"}
            </p>
            <p className="text-[10px] text-muted-foreground/50">
              (key: {extension || "none selected"})
            </p>
          </div>
          <div className="space-y-1">
            <label className="text-muted-foreground/70" htmlFor="manual-capi-token">
              CAPI bearer token
            </label>
            <Input
              id="manual-capi-token"
              type="password"
              autoComplete="off"
              value={token}
              onChange={(e) => setToken(e.target.value)}
              placeholder="Paste the bearer string here…"
              className="bg-glass font-mono text-xs"
            />
            <p className="text-[10px] text-muted-foreground/50">
              Stored encrypted (DataProtection key-ring); never written to
              logs or audit payloads. Invalidating the token must be done
              in the Telavox webapp — SD's revoke is local-only for
              manual links.
            </p>
          </div>
        </div>

        <DialogFooter>
          <Button
            size="sm"
            variant="ghost"
            onClick={() => onOpenChange(false)}
            disabled={busy}
          >
            Cancel
          </Button>
          <Button
            size="sm"
            disabled={!canSubmit}
            onClick={() => onConfirm(token.trim())}
          >
            Link
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
