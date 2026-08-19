import * as React from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import {
  AlertTriangle,
  CheckCircle2,
  ExternalLink,
  KeyRound,
  Mail,
  RefreshCw,
  Send,
  ShieldCheck,
  Ticket,
  UserPlus,
  UserRound,
  XCircle,
} from "lucide-react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { SettingField } from "@/components/settings/SettingField";
import { settingsApi, taxonomyApi, type SettingEntry, apiErrorMessage } from "@/lib/api";
import { portalAdminApi, type PortalAccount, type PortalAccountStatus, type PortalInvitation } from "@/lib/portal-api";
import { useAuth } from "@/auth/authStore";
import { useServerTime, toServerLocal } from "@/hooks/useServerTime";
import { cn } from "@/lib/utils";
import {
  PORTAL_ADMIN_QK,
  PortalAccountActions,
  PortalApproveDialog,
  PortalInviteDialog,
  PortalRejectDialog,
  PortalStatusChip,
  usePortalAdminInvalidate,
} from "@/components/portal/PortalAccountDialogs";

const PORTAL_QK = ["settings", "list", "Portal"] as const;
const TICKETS_QK = ["settings", "list", "Tickets"] as const;
const STATUS_QK = [...PORTAL_ADMIN_QK, "status"] as const;

function findEntry(entries: SettingEntry[] | undefined, key: string) {
  return entries?.find((e) => e.key === key);
}

export function PortalSettingsPage() {
  const portal = useQuery({ queryKey: PORTAL_QK, queryFn: () => settingsApi.list("Portal") });
  const tickets = useQuery({ queryKey: TICKETS_QK, queryFn: () => settingsApi.list("Tickets") });
  const status = useQuery({ queryKey: STATUS_QK, queryFn: () => portalAdminApi.status() });
  const loading = portal.isLoading;
  const e = (key: string) => findEntry(portal.data, key);

  return (
    <div className="space-y-6" data-testid="portal-settings">
      <header className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <h1 className="font-display text-display-sm tracking-tight">Customer portal</h1>
          <p className="mt-1 max-w-2xl text-sm text-muted-foreground">
            Customers sign in with their own flow (password + mandatory authenticator app) and see the tickets of their contact or
            company. Registrations need approval; invitations skip it.
          </p>
        </div>
        <a
          href="/portal/login"
          target="_blank"
          rel="noopener noreferrer"
          className="inline-flex items-center gap-1.5 rounded-md border border-glass bg-glass px-3 py-1.5 text-xs text-foreground hover:bg-glass-hover"
        >
          <ExternalLink className="h-3.5 w-3.5" /> Open the portal sign-in
        </a>
      </header>

      <StatusStrip status={status.data} loading={status.isLoading} />

      <AccountsPanel />

      <section className="glass-card p-6">
        <SectionTitle icon={UserRound} title="General" hint="Master switch, naming and the portal's ticket queue." />
        {loading ? (
          <FieldSkeleton />
        ) : (
          <div className="divide-y divide-glass">
            <Field entry={e("Portal.Enabled")} label="Portal enabled" hint="Off = every portal endpoint answers 404 and customer sessions are refused." />
            <Field entry={e("Portal.OrganisationName")} label="Organisation name" hint="Shown in portal mail and page footers. Empty = Servicedesk." />
            <QueueSettingRow entry={e("Portal.NewTicketQueueId")} label="Queue for new portal tickets" hint="Empty = customers can reply but not create tickets." emptyLabel="Disabled — no ticket creation from the portal" />
            <Field entry={e("Portal.AllowReplyOnResolved")} label="Allow replies on resolved tickets" hint="Closed tickets are never writable from the portal." />
            <Field entry={e("Portal.TicketPageSize")} label="Tickets per page" />
            <Field entry={e("Portal.SessionLifetimeHours")} label="Customer session lifetime (hours)" />
          </div>
        )}
      </section>

      <section className="glass-card p-6">
        <SectionTitle icon={UserPlus} title="Registration & approval" hint="Self-registration flow: email confirmation → approval by an agent or admin." />
        {loading || tickets.isLoading ? (
          <FieldSkeleton />
        ) : (
          <div className="divide-y divide-glass">
            <Field entry={e("Portal.RegistrationEnabled")} label="Allow self-registration" hint="Off = only invited accounts can sign in." />
            <Field
              entry={findEntry(tickets.data, "Tickets.NewUserCreatesNotificationTicket")}
              queryKey={TICKETS_QK}
              label="Create a ticket for each verified registration"
              hint="The team approves or rejects from the card on that ticket. Needs the queue below."
            />
            <QueueSettingRow entry={e("Portal.RegistrationQueueId")} label="Queue for registration tickets" hint="Also the fallback sender mailbox for portal mail." emptyLabel="Not set — approvals from this page only" />
            <Field entry={e("Portal.VerificationTokenHours")} label="Confirmation link validity (hours)" />
            <Field entry={e("Portal.InvitationTokenHours")} label="Invitation link validity (hours)" />
            <Field entry={e("Portal.PasswordResetTokenMinutes")} label="Password-reset link validity (minutes)" />
            <Field entry={e("Portal.MailResendCooldownMinutes")} label="Mail resend cooldown (minutes)" />
            <Field entry={e("Portal.PasswordMinimumLength")} label="Minimum password length" />
            <TextAreaSetting entry={e("Portal.RegistrationIntroHtml")} label="Text above the registration form" hint="Optional. Basic HTML allowed; sanitised on render." rows={3} />
          </div>
        )}
      </section>

      <section className="glass-card p-6">
        <SectionTitle icon={ShieldCheck} title="Anti-bot — Cloudflare Turnstile" hint="Protects the registration form. The secret key is stored encrypted and never shown again." />
        {loading ? (
          <FieldSkeleton />
        ) : (
          <div className="divide-y divide-glass">
            <Field entry={e("Portal.Turnstile.Enabled")} label="Turnstile enabled" hint="When on without a secret key, registration is refused (fail closed)." />
            <Field entry={e("Portal.Turnstile.SiteKey")} label="Site key (public)" />
            <Field entry={e("Portal.Turnstile.Action")} label="Action name" hint="Stamped on the widget and verified server-side." />
            <Field entry={e("Portal.Turnstile.TimeoutSeconds")} label="Verification timeout (seconds)" />
            <TurnstileSecretRow />
          </div>
        )}
        {status.data?.turnstile.misconfigured ? (
          <p className="mt-3 flex items-center gap-2 text-xs text-amber-600 dark:text-amber-300">
            <AlertTriangle className="h-3.5 w-3.5" /> Turnstile is enabled but the site key or the secret is missing — registrations are currently refused.
          </p>
        ) : null}
      </section>

      <section className="glass-card p-6">
        <SectionTitle icon={Mail} title="Mail" hint="Sender and the four transactional templates. Placeholders: {{name}}, {{email}}, {{link}}, {{expires}}, {{organisation}}." />
        {loading ? (
          <FieldSkeleton />
        ) : (
          <div className="divide-y divide-glass">
            <Field entry={e("Portal.FromMailbox")} label="Sender mailbox" hint={`Resolved now: ${status.data?.fromMailbox ?? "— none (mail cannot be sent)"}`} />
            <Field entry={e("Portal.FromName")} label="Sender display name" />
            <TemplatePair subject={e("Portal.Mail.Verification.Subject")} body={e("Portal.Mail.Verification.Body")} title="Email confirmation" />
            <TemplatePair subject={e("Portal.Mail.Approved.Subject")} body={e("Portal.Mail.Approved.Body")} title="Registration approved" hint="Empty subject = not sent." />
            <TemplatePair subject={e("Portal.Mail.Invitation.Subject")} body={e("Portal.Mail.Invitation.Body")} title="Invitation" />
            <TemplatePair subject={e("Portal.Mail.PasswordReset.Subject")} body={e("Portal.Mail.PasswordReset.Body")} title="Password reset" />
          </div>
        )}
      </section>

      <section className="glass-card p-6">
        <SectionTitle icon={KeyRound} title="Rate limits" hint="Display-only mirrors of Security:RateLimit:PortalAuth / PortalRegister in the host configuration — read at startup." />
        {loading ? (
          <FieldSkeleton />
        ) : (
          <div className="divide-y divide-glass">
            <Field entry={e("Portal.RateLimit.Auth.PermitPerWindow")} label="Sign-in / 2FA / reset requests per window (per IP)" />
            <Field entry={e("Portal.RateLimit.Auth.WindowSeconds")} label="Window (seconds)" />
            <Field entry={e("Portal.RateLimit.Register.PermitPerWindow")} label="Registration / forgot-password per window (per IP)" />
            <Field entry={e("Portal.RateLimit.Register.WindowSeconds")} label="Window (seconds)" />
          </div>
        )}
      </section>
    </div>
  );
}

// ---- pieces ---------------------------------------------------------------------

function SectionTitle({ icon: Icon, title, hint }: { icon: typeof UserRound; title: string; hint: string }) {
  return (
    <div className="mb-3">
      <h2 className="flex items-center gap-2 text-sm font-semibold">
        <Icon className="h-4 w-4 text-primary" /> {title}
      </h2>
      <p className="mt-0.5 text-xs text-muted-foreground">{hint}</p>
    </div>
  );
}

function FieldSkeleton() {
  return (
    <div className="space-y-3">
      {Array.from({ length: 4 }).map((_, i) => (
        <Skeleton key={i} className="h-9 w-full" />
      ))}
    </div>
  );
}

function Field({ entry, label, hint, queryKey }: { entry: SettingEntry | undefined; label: string; hint?: string; queryKey?: readonly unknown[] }) {
  if (!entry) return <p className="py-2 text-xs text-muted-foreground">Setting not seeded yet — restart the app.</p>;
  return <SettingField entry={entry} queryKey={queryKey ?? PORTAL_QK} label={label} hint={hint} />;
}

function StatusStrip({ status, loading }: { status: ReturnType<typeof portalAdminApi.status> extends Promise<infer T> ? T | undefined : never; loading: boolean }) {
  if (loading || !status) return <Skeleton className="h-14 w-full" />;
  const items: { ok: boolean; label: string }[] = [
    { ok: status.enabled, label: status.enabled ? "Portal enabled" : "Portal disabled" },
    { ok: status.publicBaseUrlConfigured, label: status.publicBaseUrlConfigured ? "Public URL set" : "App.PublicBaseUrl missing — mail links break" },
    { ok: !!status.fromMailbox, label: status.fromMailbox ? `Mail from ${status.fromMailbox}` : "No sender mailbox" },
    { ok: status.newTicketQueueConfigured, label: status.newTicketQueueConfigured ? "New-ticket queue set" : "No new-ticket queue (replies only)" },
    {
      ok: !status.registrationTicketEnabled || status.registrationQueueConfigured,
      label: status.registrationTicketEnabled
        ? status.registrationQueueConfigured
          ? "Registration tickets on"
          : "Registration tickets on but no queue"
        : "Registration tickets off",
    },
    {
      ok: !status.turnstile.misconfigured,
      label: status.turnstile.enabled ? (status.turnstile.misconfigured ? "Turnstile misconfigured" : "Turnstile on") : "Turnstile off",
    },
  ];
  return (
    <div className="glass-card flex flex-wrap items-center gap-x-4 gap-y-2 px-4 py-3 text-xs">
      {items.map((it) => (
        <span key={it.label} className="inline-flex items-center gap-1.5">
          {it.ok ? <CheckCircle2 className="h-3.5 w-3.5 text-emerald-500" /> : <XCircle className="h-3.5 w-3.5 text-amber-500" />}
          {it.label}
        </span>
      ))}
      <span className="ml-auto text-muted-foreground">
        {status.counts.pendingApproval} awaiting approval · {status.counts.active} active
      </span>
    </div>
  );
}

function QueueSettingRow({ entry, label, hint, emptyLabel }: { entry: SettingEntry | undefined; label: string; hint: string; emptyLabel: string }) {
  const qc = useQueryClient();
  const queues = useQuery({ queryKey: ["taxonomy", "queues"], queryFn: () => taxonomyApi.queues.list() });
  const save = useMutation({
    mutationFn: (value: string) => settingsApi.update(entry!.key, value),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: PORTAL_QK });
      qc.invalidateQueries({ queryKey: STATUS_QK });
      toast.success(`${label} updated`);
    },
    onError: (err) => toast.error(apiErrorMessage(err) ?? "Save failed"),
  });
  if (!entry) return null;
  return (
    <div className="flex flex-wrap items-center justify-between gap-3 py-3">
      <div className="min-w-0">
        <p className="text-sm font-medium">{label}</p>
        <p className="text-xs text-muted-foreground">{hint}</p>
      </div>
      <select
        value={entry.value}
        disabled={save.isPending || queues.isLoading}
        onChange={(ev) => save.mutate(ev.target.value)}
        className="h-9 w-full max-w-xs rounded-md border border-glass bg-glass px-2 text-sm text-foreground outline-none focus-visible:ring-2 focus-visible:ring-ring"
      >
        <option value="">{emptyLabel}</option>
        {(queues.data ?? []).map((q) => (
          <option key={q.id} value={q.id}>
            {q.name}
          </option>
        ))}
      </select>
    </div>
  );
}

function TextAreaSetting({ entry, label, hint, rows }: { entry: SettingEntry | undefined; label: string; hint?: string; rows: number }) {
  const qc = useQueryClient();
  const [draft, setDraft] = React.useState(entry?.value ?? "");
  React.useEffect(() => setDraft(entry?.value ?? ""), [entry?.value]);
  const save = useMutation({
    mutationFn: (val: string) => settingsApi.update(entry!.key, val),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: PORTAL_QK });
      toast.success(`${label} updated`);
    },
    onError: (err) => {
      toast.error(apiErrorMessage(err) ?? "Save failed");
      setDraft(entry?.value ?? "");
    },
  });
  if (!entry) return null;
  const dirty = draft !== entry.value;
  return (
    <div className="space-y-2 py-3">
      <div className="flex items-start justify-between gap-4">
        <div className="min-w-0 flex-1">
          <p className="text-sm font-medium">{label}</p>
          {hint ? <p className="mt-0.5 text-xs text-muted-foreground">{hint}</p> : null}
        </div>
        <div className="flex shrink-0 items-center gap-2">
          <Button size="sm" variant="ghost" disabled={!dirty || save.isPending} onClick={() => save.mutate(draft)} className="h-8 px-3">
            Save
          </Button>
          {dirty ? (
            <Button size="sm" variant="ghost" disabled={save.isPending} onClick={() => setDraft(entry.value)} className="h-8 px-2 text-xs text-muted-foreground">
              Reset
            </Button>
          ) : null}
        </div>
      </div>
      <textarea
        value={draft}
        onChange={(ev) => setDraft(ev.target.value)}
        disabled={save.isPending}
        rows={rows}
        spellCheck={false}
        className="w-full rounded-md border border-glass bg-glass px-3 py-2 font-mono text-[11px] leading-relaxed text-foreground/90 outline-none focus-visible:ring-2 focus-visible:ring-ring disabled:opacity-50"
      />
    </div>
  );
}

function TemplatePair({ subject, body, title, hint }: { subject: SettingEntry | undefined; body: SettingEntry | undefined; title: string; hint?: string }) {
  return (
    <div className="py-2">
      <p className="pt-2 text-xs font-semibold uppercase tracking-[0.14em] text-muted-foreground">{title}</p>
      {hint ? <p className="text-[11px] text-muted-foreground">{hint}</p> : null}
      <Field entry={subject} label="Subject" />
      <TextAreaSetting entry={body} label="Body (HTML)" rows={6} />
    </div>
  );
}

function TurnstileSecretRow() {
  const qc = useQueryClient();
  const secret = useQuery({ queryKey: [...PORTAL_ADMIN_QK, "turnstile-secret"], queryFn: () => portalAdminApi.turnstileSecretStatus() });
  const [draft, setDraft] = React.useState("");
  const invalidate = () => {
    qc.invalidateQueries({ queryKey: [...PORTAL_ADMIN_QK, "turnstile-secret"] });
    qc.invalidateQueries({ queryKey: STATUS_QK });
  };
  const save = useMutation({
    mutationFn: () => portalAdminApi.setTurnstileSecret(draft),
    onSuccess: () => {
      toast.success("Turnstile secret saved");
      setDraft("");
      invalidate();
    },
    onError: (err) => toast.error(apiErrorMessage(err) ?? "Save failed"),
  });
  const clear = useMutation({
    mutationFn: () => portalAdminApi.deleteTurnstileSecret(),
    onSuccess: () => {
      toast.success("Turnstile secret cleared");
      invalidate();
    },
    onError: (err) => toast.error(apiErrorMessage(err) ?? "Clear failed"),
  });
  const configured = secret.data?.configured ?? false;
  return (
    <div className="flex flex-wrap items-center justify-between gap-3 py-3">
      <div className="min-w-0">
        <p className="text-sm font-medium">Secret key</p>
        <p className="text-xs text-muted-foreground">
          {configured ? "A secret key is stored (encrypted). Paste a new one to replace it." : "No secret key stored yet."}
        </p>
      </div>
      <div className="flex items-center gap-2">
        <Input
          type="password"
          autoComplete="new-password"
          value={draft}
          onChange={(ev) => setDraft(ev.target.value)}
          placeholder={configured ? "••••••• (enter to replace)" : "Paste Turnstile secret key"}
          className="w-64"
        />
        <Button size="sm" onClick={() => save.mutate()} disabled={!draft.trim() || save.isPending}>
          Save
        </Button>
        {configured ? (
          <Button size="sm" variant="ghost" onClick={() => clear.mutate()} disabled={clear.isPending}>
            Clear
          </Button>
        ) : null}
      </div>
    </div>
  );
}

// ---- accounts panel ------------------------------------------------------------

type Tab = "PendingApproval" | "PendingVerification" | "Active" | "Deactivated" | "Rejected" | "Invitations";

const TABS: { key: Tab; label: string }[] = [
  { key: "PendingApproval", label: "Awaiting approval" },
  { key: "PendingVerification", label: "Awaiting confirmation" },
  { key: "Active", label: "Active" },
  { key: "Deactivated", label: "Deactivated" },
  { key: "Rejected", label: "Rejected" },
  { key: "Invitations", label: "Invitations" },
];

function AccountsPanel() {
  const { user } = useAuth();
  const isAdmin = user?.role === "Admin";
  const invalidate = usePortalAdminInvalidate();
  const { time } = useServerTime();
  const fmt = (iso: string | null) => (!iso ? "—" : time ? toServerLocal(iso, time.offsetMinutes) : new Date(iso).toLocaleString());
  const [tab, setTab] = React.useState<Tab>("PendingApproval");
  const [search, setSearch] = React.useState("");
  const [inviteOpen, setInviteOpen] = React.useState(false);
  const [approveTarget, setApproveTarget] = React.useState<PortalAccount | null>(null);
  const [rejectTarget, setRejectTarget] = React.useState<PortalAccount | null>(null);
  const status = useQuery({ queryKey: STATUS_QK, queryFn: () => portalAdminApi.status() });

  const accounts = useQuery({
    queryKey: [...PORTAL_ADMIN_QK, "accounts", tab, search],
    queryFn: () => portalAdminApi.listAccounts(tab === "Invitations" ? null : [tab as PortalAccountStatus], search),
    enabled: tab !== "Invitations",
  });
  const invitations = useQuery({
    queryKey: [...PORTAL_ADMIN_QK, "invitations"],
    queryFn: () => portalAdminApi.listInvitations(undefined, true),
    enabled: tab === "Invitations",
  });
  const suggestion = useQuery({
    queryKey: [...PORTAL_ADMIN_QK, "account", approveTarget?.userId],
    queryFn: () => portalAdminApi.getAccount(approveTarget!.userId),
    enabled: !!approveTarget,
  });

  const resend = useMutation({
    mutationFn: (id: string) => portalAdminApi.resendInvitation(id),
    onSuccess: () => {
      toast.success("Invitation re-sent");
      invalidate();
    },
    onError: (err) => toast.error(apiErrorMessage(err) ?? "Could not resend"),
  });
  const revoke = useMutation({
    mutationFn: (id: string) => portalAdminApi.revokeInvitation(id),
    onSuccess: () => {
      toast.success("Invitation revoked");
      invalidate();
    },
    onError: (err) => toast.error(apiErrorMessage(err) ?? "Could not revoke"),
  });

  const counts = status.data?.counts;
  const badge = (t: Tab) =>
    t === "PendingApproval" ? counts?.pendingApproval : t === "PendingVerification" ? counts?.pendingVerification : t === "Active" ? counts?.active : t === "Deactivated" ? counts?.deactivated : t === "Rejected" ? counts?.rejected : undefined;

  return (
    <section className="glass-card overflow-hidden" data-testid="portal-accounts">
      <div className="flex flex-wrap items-center gap-3 border-b border-glass px-4 py-3">
        <h2 className="flex items-center gap-2 text-sm font-semibold">
          <Ticket className="h-4 w-4 text-primary" /> Accounts
        </h2>
        <div className="flex flex-wrap items-center gap-1">
          {TABS.map((t) => (
            <button
              key={t.key}
              type="button"
              onClick={() => setTab(t.key)}
              className={cn(
                "inline-flex items-center gap-1.5 rounded-md px-2.5 py-1 text-xs transition-colors",
                tab === t.key ? "bg-glass-strong text-foreground" : "text-muted-foreground hover:bg-glass-hover hover:text-foreground",
              )}
            >
              {t.label}
              {badge(t.key) !== undefined && badge(t.key)! > 0 ? (
                <span className={cn("rounded-full px-1.5 text-[10px]", t.key === "PendingApproval" ? "bg-amber-500/20 text-amber-700 dark:text-amber-300" : "bg-glass text-muted-foreground")}>
                  {badge(t.key)}
                </span>
              ) : null}
            </button>
          ))}
        </div>
        <div className="ml-auto flex items-center gap-2">
          {tab !== "Invitations" ? <Input value={search} onChange={(ev) => setSearch(ev.target.value)} placeholder="Search email, name, company" className="h-8 w-56" /> : null}
          <Button size="sm" className="gap-1.5" onClick={() => setInviteOpen(true)}>
            <Send className="h-3.5 w-3.5" /> Invite
          </Button>
          <Button size="sm" variant="ghost" onClick={() => invalidate()} aria-label="Refresh">
            <RefreshCw className="h-3.5 w-3.5" />
          </Button>
        </div>
      </div>

      {tab === "Invitations" ? (
        invitations.isLoading ? (
          <div className="p-4">
            <Skeleton className="h-24 w-full" />
          </div>
        ) : !invitations.data || invitations.data.length === 0 ? (
          <p className="p-6 text-center text-sm text-muted-foreground">No open invitations.</p>
        ) : (
          <table className="w-full text-sm">
            <thead className="sd-table-head text-left text-[11px] uppercase tracking-[0.14em] text-muted-foreground">
              <tr className="border-b border-glass">
                <th className="px-4 py-2 font-medium">Invitee</th>
                <th className="px-4 py-2 font-medium">Company · role</th>
                <th className="px-4 py-2 font-medium">Sent</th>
                <th className="px-4 py-2 font-medium">Expires</th>
                <th className="px-4 py-2" />
              </tr>
            </thead>
            <tbody>
              {invitations.data.map((inv: PortalInvitation) => (
                <tr key={inv.id} className="border-b border-glass last:border-0">
                  <td className="px-4 py-2">
                    <div className="font-medium">{inv.displayName || inv.email}</div>
                    <div className="text-xs text-muted-foreground">{inv.email}</div>
                  </td>
                  <td className="px-4 py-2 text-xs text-muted-foreground">
                    {inv.companyName ?? "—"} · {inv.companyRole ?? "Member"}
                  </td>
                  <td className="px-4 py-2 text-xs text-muted-foreground">
                    {fmt(inv.createdUtc)}
                    {inv.createdByEmail ? <div className="text-[11px]">by {inv.createdByEmail}</div> : null}
                  </td>
                  <td className={cn("px-4 py-2 text-xs", inv.expired ? "text-amber-600 dark:text-amber-300" : "text-muted-foreground")}>
                    {inv.expired ? "Expired " : ""}
                    {fmt(inv.expiresUtc)}
                  </td>
                  <td className="px-4 py-2 text-right">
                    <div className="inline-flex gap-1.5">
                      <Button size="sm" variant="outline" onClick={() => resend.mutate(inv.id)} disabled={resend.isPending}>
                        Resend
                      </Button>
                      <Button size="sm" variant="ghost" onClick={() => revoke.mutate(inv.id)} disabled={revoke.isPending}>
                        Revoke
                      </Button>
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )
      ) : accounts.isLoading ? (
        <div className="p-4">
          <Skeleton className="h-24 w-full" />
        </div>
      ) : !accounts.data || accounts.data.length === 0 ? (
        <p className="p-6 text-center text-sm text-muted-foreground">Nothing here.</p>
      ) : (
        <table className="w-full text-sm">
          <thead className="sd-table-head text-left text-[11px] uppercase tracking-[0.14em] text-muted-foreground">
            <tr className="border-b border-glass">
              <th className="px-4 py-2 font-medium">Account</th>
              <th className="px-4 py-2 font-medium">Contact · company</th>
              <th className="px-4 py-2 font-medium">Status</th>
              <th className="px-4 py-2 font-medium">Since</th>
              <th className="px-4 py-2" />
            </tr>
          </thead>
          <tbody>
            {accounts.data.map((a) => (
              <tr key={a.userId} className="border-b border-glass last:border-0 align-top">
                <td className="px-4 py-2">
                  <div className="font-medium">{a.displayName || a.email}</div>
                  <div className="text-xs text-muted-foreground">{a.email}</div>
                  <div className="mt-0.5 text-[11px] text-muted-foreground">
                    {a.origin === "Invitation" ? `Invited${a.invitedByEmail ? ` by ${a.invitedByEmail}` : ""}` : `Registered${a.registrationIp ? ` from ${a.registrationIp}` : ""}`}
                    {a.twoFactorEnrolled ? " · 2FA set up" : ""}
                    {a.approvalTicketId ? (
                      <>
                        {" · "}
                        <Link to="/tickets/$ticketId" params={{ ticketId: a.approvalTicketId }} className="text-primary hover:underline">
                          ticket #{a.approvalTicketNumber}
                        </Link>
                      </>
                    ) : null}
                  </div>
                </td>
                <td className="px-4 py-2 text-xs text-muted-foreground">
                  {a.contactId ? (
                    <Link to="/contacts/$contactId" params={{ contactId: a.contactId }} className="text-primary hover:underline">
                      {a.contactName || a.email}
                    </Link>
                  ) : (
                    "Not linked yet"
                  )}
                  <div>
                    {a.companyName ?? "No company"} {a.companyRole ? `· ${a.companyRole}` : ""}
                  </div>
                </td>
                <td className="px-4 py-2">
                  <PortalStatusChip status={a.status} />
                  {a.status === "Rejected" && a.rejectionReason ? <div className="mt-1 max-w-[220px] text-[11px] text-muted-foreground">{a.rejectionReason}</div> : null}
                </td>
                <td className="px-4 py-2 text-xs text-muted-foreground">
                  {fmt(a.createdUtc)}
                  {a.lastLoginUtc ? <div className="text-[11px]">last sign-in {fmt(a.lastLoginUtc)}</div> : null}
                </td>
                <td className="px-4 py-2 text-right">
                  <PortalAccountActions account={a} isAdmin={isAdmin} compact onApprove={() => setApproveTarget(a)} onReject={() => setRejectTarget(a)} />
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}

      <PortalInviteDialog open={inviteOpen} onClose={() => setInviteOpen(false)} />
      <PortalApproveDialog
        open={!!approveTarget}
        onClose={() => setApproveTarget(null)}
        account={approveTarget}
        suggestedCompany={suggestion.data?.suggestedCompany ?? null}
      />
      <PortalRejectDialog open={!!rejectTarget} onClose={() => setRejectTarget(null)} account={rejectTarget} />
    </section>
  );
}
