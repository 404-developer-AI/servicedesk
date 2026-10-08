import * as React from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useSearch } from "@tanstack/react-router";
import { toast } from "sonner";
import { Ban, ShieldAlert, ShieldCheck, ShieldOff, SlidersHorizontal, TriangleAlert } from "lucide-react";
import {
  ApiError,
  apiErrorMessage,
  ipBlockingApi,
  type IpProposal,
  type IpRule,
} from "@/lib/api";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { CollapsibleSettingsCard } from "@/components/settings/CollapsibleSettingsCard";
import { useServerTimeZone } from "@/hooks/useServerTime";
import { formatDateTime } from "@/lib/dateFormat";
import { cn } from "@/lib/utils";

const QUERY_KEY = ["admin", "ip-blocking"] as const;

const IP_BLOCKING_SETTINGS: ReadonlyArray<{ key: string; label: string }> = [
  { key: "Security.IpBlocking.Enabled", label: "Detection enabled" },
  { key: "Security.IpBlocking.WindowMinutes", label: "Counting window (minutes)" },
  { key: "Security.IpBlocking.ScannerThreshold", label: "Scanner probes before auto-block" },
  { key: "Security.IpBlocking.AutoBlockHours", label: "Automatic block duration (hours)" },
  { key: "Security.IpBlocking.RateLimitThreshold", label: "Rate-limit rejections before a proposal" },
  { key: "Security.IpBlocking.CsrfThreshold", label: "CSRF rejections before a proposal" },
  { key: "Security.IpBlocking.FailedLoginThreshold", label: "Failed sign-ins before a proposal" },
  { key: "Security.IpBlocking.ScannerPaths", label: "Scanner path patterns" },
];

const REASON_LABEL: Record<string, string> = {
  scanner_paths: "Scanner probes",
  rate_limited: "Rate-limit bursts",
  csrf_rejected: "CSRF rejections",
  failed_logins: "Failed sign-ins",
};

type Decision =
  | { kind: "block"; proposal: IpProposal; needsConfirm: boolean; knownLogins?: number }
  | { kind: "whitelist"; proposal: IpProposal }
  | { kind: "add"; ip: string; ruleKind: "block" | "whitelist"; reason: string; knownLogins: number };

/// Settings → IP blocking. Admins decide on block proposals raised by the
/// server-side detector, and manage the block / whitelist rules. Every
/// safeguard (own IP, proxy network, known sign-ins) is enforced server-side;
/// this page only surfaces the confirmations.
export function IpBlockingPage() {
  const tz = useServerTimeZone();
  const queryClient = useQueryClient();
  const search = useSearch({ strict: false }) as { ip?: string };
  const [filter, setFilter] = React.useState(search.ip ?? "");
  const [decision, setDecision] = React.useState<Decision | null>(null);

  const overview = useQuery({
    queryKey: QUERY_KEY,
    queryFn: () => ipBlockingApi.overview(),
    refetchInterval: 30_000,
  });

  const refresh = () => {
    void queryClient.invalidateQueries({ queryKey: QUERY_KEY });
    // The health card (and the dashboard pill) count open proposals.
    void queryClient.invalidateQueries({ queryKey: ["admin", "health"] });
    void queryClient.invalidateQueries({ queryKey: ["system", "health"] });
  };

  const onError = (err: unknown) => toast.error(apiErrorMessage(err) ?? "Action failed");

  const dismiss = useMutation({
    mutationFn: (p: IpProposal) => ipBlockingApi.dismiss(p.id),
    onSuccess: (_d, p) => {
      toast.success(p.autoBlocked ? `${p.ip} released` : `Proposal for ${p.ip} dismissed`);
      refresh();
    },
    onError,
  });

  const removeRule = useMutation({
    mutationFn: (r: IpRule) => ipBlockingApi.removeRule(r.ip),
    onSuccess: (_d, r) => {
      toast.success(r.kind === "block" ? `${r.ip} unblocked` : `${r.ip} removed from the whitelist`);
      refresh();
    },
    onError,
  });

  const data = overview.data;
  const needle = filter.trim();
  const match = (ip: string) => needle.length === 0 || ip.includes(needle);
  const open = (data?.openProposals ?? []).filter((p) => match(p.ip));
  const blocked = (data?.rules ?? []).filter((r) => r.kind === "block" && match(r.ip));
  const whitelisted = (data?.rules ?? []).filter((r) => r.kind === "whitelist" && match(r.ip));
  const recent = (data?.recentProposals ?? []).filter((p) => match(p.ip));

  return (
    <div className="flex w-full flex-col gap-6">
      <header className="flex items-start justify-between gap-4">
        <div className="space-y-1">
          <h1 className="text-display-md font-semibold text-foreground">IP blocking</h1>
          <p className="max-w-3xl text-sm text-muted-foreground">
            Addresses that probe for leaked files or trip abuse limits are raised here for a decision.
            Obvious vulnerability scanners are blocked temporarily right away; confirm to block them for
            good. Whitelisted addresses get no further proposals — logging and rate limits still apply.
          </p>
        </div>
        <div className="flex shrink-0 flex-col items-end gap-1.5">
          <Badge className="border border-glass bg-glass text-xs font-normal text-muted-foreground">
            Admin only
          </Badge>
          {data?.yourIp && (
            <span className="text-xs text-muted-foreground">
              Your address: <span className="font-mono text-foreground">{data.yourIp}</span>
            </span>
          )}
        </div>
      </header>

      {data && !data.enabled && (
        <div className="glass-card flex items-center gap-3 border-amber-400/30 p-4 text-sm text-foreground">
          <TriangleAlert className="h-4 w-4 shrink-0 text-amber-500" />
          Detection is switched off — no new proposals or automatic blocks. Existing blocks are still enforced.
        </div>
      )}

      <div className="flex flex-wrap items-center gap-3">
        <Input
          value={filter}
          onChange={(e) => setFilter(e.target.value)}
          placeholder="Filter by address…"
          className="h-9 w-64 font-mono"
          aria-label="Filter by address"
        />
        <AddRuleForm onDecision={setDecision} onDone={refresh} />
      </div>

      {overview.isLoading ? (
        <div className="space-y-3">
          <Skeleton className="h-28 w-full" />
          <Skeleton className="h-40 w-full" />
        </div>
      ) : overview.isError ? (
        <div className="glass-card p-6 text-center text-sm text-destructive">
          Failed to load IP blocking data.
        </div>
      ) : (
        <>
          <section className="space-y-3">
            <SectionTitle icon={<ShieldAlert className="h-4 w-4" />} title="Waiting for a decision" count={open.length} />
            {open.length === 0 ? (
              <EmptyCard text="No block proposals. Suspicious addresses will appear here." />
            ) : (
              open.map((p) => (
                <ProposalCard
                  key={p.id}
                  proposal={p}
                  tz={tz}
                  busy={dismiss.isPending}
                  onBlock={() => setDecision({ kind: "block", proposal: p, needsConfirm: p.knownLogin })}
                  onWhitelist={() => setDecision({ kind: "whitelist", proposal: p })}
                  onDismiss={() => dismiss.mutate(p)}
                />
              ))
            )}
          </section>

          <section className="space-y-3">
            <SectionTitle icon={<Ban className="h-4 w-4" />} title="Blocked addresses" count={blocked.length} />
            <RuleTable
              rules={blocked}
              tz={tz}
              empty="Nothing is blocked."
              actionLabel="Unblock"
              busy={removeRule.isPending}
              onRemove={(r) => removeRule.mutate(r)}
            />
          </section>

          <section className="space-y-3">
            <SectionTitle icon={<ShieldCheck className="h-4 w-4" />} title="Whitelisted addresses" count={whitelisted.length} />
            <RuleTable
              rules={whitelisted}
              tz={tz}
              empty="No whitelisted addresses."
              actionLabel="Remove"
              busy={removeRule.isPending}
              onRemove={(r) => removeRule.mutate(r)}
            />
          </section>

          {recent.length > 0 && (
            <section className="space-y-3">
              <SectionTitle icon={<ShieldOff className="h-4 w-4" />} title="Recently decided" count={recent.length} />
              <div className="glass-card overflow-x-auto">
                <table className="w-full text-left text-sm">
                  <thead className="text-xs uppercase tracking-wide text-muted-foreground [&_th]:border-b [&_th]:border-glass">
                    <tr>
                      <th className="px-4 py-2.5 font-medium">Address</th>
                      <th className="px-4 py-2.5 font-medium">Outcome</th>
                      <th className="px-4 py-2.5 font-medium">Reasons</th>
                      <th className="px-4 py-2.5 font-medium">Decided</th>
                    </tr>
                  </thead>
                  <tbody>
                    {recent.map((p) => (
                      <tr key={p.id} className="border-b border-glass last:border-b-0">
                        <td className="px-4 py-2.5 font-mono text-xs text-foreground">{p.ip}</td>
                        <td className="px-4 py-2.5 text-xs capitalize text-foreground">{p.status}</td>
                        <td className="px-4 py-2.5 text-xs text-muted-foreground">
                          {p.reasons.map((r) => REASON_LABEL[r] ?? r).join(", ")}
                        </td>
                        <td className="px-4 py-2.5 text-xs text-muted-foreground">
                          {formatDateTime(p.decidedUtc, tz)} · {p.decidedBy ?? "—"}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </section>
          )}
        </>
      )}

      <CollapsibleSettingsCard
        category="Security"
        icon={<SlidersHorizontal className="h-5 w-5" />}
        title="Detection settings"
        description="What counts as suspicious and how quickly a proposal is raised. Scanner probes from an address that never signed in are blocked automatically for the configured hours; every other signal only raises a proposal. Changes apply within a minute."
        keys={IP_BLOCKING_SETTINGS}
      />

      {decision && (
        <DecisionDialog decision={decision} onClose={() => setDecision(null)} onDone={refresh} />
      )}
    </div>
  );
}

function SectionTitle({ icon, title, count }: { icon: React.ReactNode; title: string; count: number }) {
  return (
    <h2 className="flex items-center gap-2 text-sm font-semibold text-foreground">
      <span className="text-primary">{icon}</span>
      {title}
      <span className="rounded-full border border-glass bg-glass px-2 py-0.5 text-[11px] font-normal text-muted-foreground">
        {count}
      </span>
    </h2>
  );
}

function EmptyCard({ text }: { text: string }) {
  return <div className="glass-card p-5 text-sm text-muted-foreground">{text}</div>;
}

function ProposalCard({
  proposal: p,
  tz,
  busy,
  onBlock,
  onWhitelist,
  onDismiss,
}: {
  proposal: IpProposal;
  tz: string | null;
  busy: boolean;
  onBlock: () => void;
  onWhitelist: () => void;
  onDismiss: () => void;
}) {
  const counts = Object.entries(p.evidence.counts ?? {});
  const paths = p.evidence.samplePaths ?? [];
  return (
    <article
      className={cn(
        "glass-card space-y-3 p-4",
        p.autoBlocked && "border-red-400/30",
      )}
      data-testid={`ip-proposal-${p.id}`}
    >
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="space-y-1.5">
          <div className="flex flex-wrap items-center gap-2">
            <span className="font-mono text-base font-semibold text-foreground">{p.ip}</span>
            {p.autoBlocked ? (
              <span className="inline-flex items-center rounded-full border border-red-400/30 bg-red-400/10 px-2 py-0.5 text-[11px] font-medium text-red-500">
                Blocked temporarily
              </span>
            ) : (
              <span className="inline-flex items-center rounded-full border border-amber-400/30 bg-amber-400/10 px-2 py-0.5 text-[11px] font-medium text-amber-600">
                Proposal
              </span>
            )}
            {p.reasons.map((r) => (
              <span key={r} className="rounded-full border border-glass bg-glass px-2 py-0.5 text-[11px] text-foreground/80">
                {REASON_LABEL[r] ?? r}
              </span>
            ))}
          </div>
          <div className="text-xs text-muted-foreground">
            First seen {formatDateTime(p.firstSeenUtc, tz)} · last seen {formatDateTime(p.lastSeenUtc, tz)}
            {counts.length > 0 && (
              <>
                {" · "}
                {counts.map(([k, v]) => `${v}× ${(REASON_LABEL[k] ?? k).toLowerCase()}`).join(", ")}
                {p.evidence.windowMinutes ? ` in ${p.evidence.windowMinutes} min` : ""}
              </>
            )}
          </div>
        </div>
        <div className="flex flex-wrap gap-2">
          <Button size="sm" variant="destructive" onClick={onBlock} disabled={busy}>
            Block permanently
          </Button>
          <Button size="sm" variant="outline" onClick={onWhitelist} disabled={busy}>
            Whitelist
          </Button>
          <Button size="sm" variant="ghost" onClick={onDismiss} disabled={busy}>
            {p.autoBlocked ? "Release" : "Dismiss"}
          </Button>
        </div>
      </div>

      {p.knownLogin && (
        <div className="flex items-start gap-2 rounded-md border border-amber-400/30 bg-amber-400/10 px-3 py-2 text-xs text-foreground">
          <TriangleAlert className="mt-0.5 h-3.5 w-3.5 shrink-0 text-amber-500" />
          Successful sign-ins came from this address — it may be an office or a colleague. Blocking it locks
          everyone working from it out.
        </div>
      )}

      {paths.length > 0 && (
        <div className="flex flex-wrap gap-1.5">
          {paths.map((path) => (
            <code key={path} className="max-w-full truncate rounded border border-glass bg-glass px-1.5 py-0.5 text-[11px] text-foreground/80">
              {path}
            </code>
          ))}
        </div>
      )}
    </article>
  );
}

function RuleTable({
  rules,
  tz,
  empty,
  actionLabel,
  busy,
  onRemove,
}: {
  rules: IpRule[];
  tz: string | null;
  empty: string;
  actionLabel: string;
  busy: boolean;
  onRemove: (r: IpRule) => void;
}) {
  if (rules.length === 0) return <EmptyCard text={empty} />;
  const isBlock = rules[0]?.kind === "block";
  return (
    <div className="glass-card overflow-x-auto">
      <table className="w-full text-left text-sm">
        <thead className="text-xs uppercase tracking-wide text-muted-foreground [&_th]:border-b [&_th]:border-glass">
          <tr>
            <th className="px-4 py-2.5 font-medium">Address</th>
            <th className="px-4 py-2.5 font-medium">Reason</th>
            {isBlock && <th className="px-4 py-2.5 font-medium">Until</th>}
            {isBlock && <th className="px-4 py-2.5 font-medium">Requests refused</th>}
            <th className="px-4 py-2.5 font-medium">Added</th>
            <th className="px-4 py-2.5" />
          </tr>
        </thead>
        <tbody>
          {rules.map((r) => (
            <tr key={r.ip} className="border-b border-glass last:border-b-0">
              <td className="px-4 py-2.5 font-mono text-xs text-foreground">{r.ip}</td>
              <td className="px-4 py-2.5 text-xs text-muted-foreground">{r.reason ?? "—"}</td>
              {isBlock && (
                <td className="px-4 py-2.5 text-xs">
                  {r.expiresUtc ? (
                    <span className="text-amber-600">{formatDateTime(r.expiresUtc, tz)} (automatic)</span>
                  ) : (
                    <span className="text-foreground">Permanent</span>
                  )}
                </td>
              )}
              {isBlock && (
                <td className="px-4 py-2.5 font-mono text-xs text-muted-foreground">
                  {r.hitCount}
                  {r.lastHitUtc ? ` · last ${formatDateTime(r.lastHitUtc, tz)}` : ""}
                </td>
              )}
              <td className="px-4 py-2.5 text-xs text-muted-foreground">
                {formatDateTime(r.createdUtc, tz)} · {r.createdBy}
              </td>
              <td className="px-4 py-2.5 text-right">
                <Button size="sm" variant="ghost" disabled={busy} onClick={() => onRemove(r)}>
                  {actionLabel}
                </Button>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function AddRuleForm({
  onDecision,
  onDone,
}: {
  onDecision: (d: Decision) => void;
  onDone: () => void;
}) {
  const [ip, setIp] = React.useState("");
  const [kind, setKind] = React.useState<"block" | "whitelist">("block");
  const [reason, setReason] = React.useState("");

  const add = useMutation({
    mutationFn: () => ipBlockingApi.addRule({ ip: ip.trim(), kind, reason: reason.trim() || undefined }),
    onSuccess: () => {
      toast.success(kind === "block" ? `${ip.trim()} blocked` : `${ip.trim()} whitelisted`);
      setIp("");
      setReason("");
      onDone();
    },
    onError: (err) => {
      const known = knownLoginsFrom(err);
      if (known !== null) {
        onDecision({ kind: "add", ip: ip.trim(), ruleKind: kind, reason: reason.trim(), knownLogins: known });
        return;
      }
      toast.error(apiErrorMessage(err) ?? "Could not add the address");
    },
  });

  return (
    <form
      className="ml-auto flex flex-wrap items-center gap-2"
      onSubmit={(e) => {
        e.preventDefault();
        if (ip.trim()) add.mutate();
      }}
    >
      <Input
        value={ip}
        onChange={(e) => setIp(e.target.value)}
        placeholder="IP address"
        className="h-9 w-44 font-mono"
        aria-label="IP address to add"
      />
      <select
        value={kind}
        onChange={(e) => setKind(e.target.value as "block" | "whitelist")}
        className="h-9 rounded-md border border-glass bg-glass px-2 text-sm text-foreground outline-hidden focus:border-primary/60"
        aria-label="Rule kind"
      >
        <option value="block" className="bg-background">Block</option>
        <option value="whitelist" className="bg-background">Whitelist</option>
      </select>
      <Input
        value={reason}
        onChange={(e) => setReason(e.target.value)}
        placeholder="Reason (optional)"
        className="h-9 w-52"
        maxLength={500}
        aria-label="Reason"
      />
      <Button type="submit" size="sm" disabled={!ip.trim() || add.isPending}>
        Add
      </Button>
    </form>
  );
}

function DecisionDialog({
  decision,
  onClose,
  onDone,
}: {
  decision: Decision;
  onClose: () => void;
  onDone: () => void;
}) {
  const [reason, setReason] = React.useState(decision.kind === "add" ? decision.reason : "");
  const [knownLogins, setKnownLogins] = React.useState<number | null>(
    decision.kind === "add" ? decision.knownLogins : null,
  );
  const [confirmed, setConfirmed] = React.useState(false);

  const ip = decision.kind === "add" ? decision.ip : decision.proposal.ip;
  const isBlock = decision.kind === "block" || (decision.kind === "add" && decision.ruleKind === "block");
  const warnKnown = isBlock && (knownLogins !== null || (decision.kind === "block" && decision.needsConfirm));

  const submit = useMutation({
    mutationFn: () => {
      const r = reason.trim() || undefined;
      if (decision.kind === "block")
        return ipBlockingApi.block(decision.proposal.id, { reason: r, confirmKnownLogins: confirmed });
      if (decision.kind === "whitelist") return ipBlockingApi.whitelist(decision.proposal.id, { reason: r });
      return ipBlockingApi.addRule({ ip: decision.ip, kind: decision.ruleKind, reason: r, confirmKnownLogins: confirmed });
    },
    onSuccess: () => {
      toast.success(isBlock ? `${ip} blocked` : `${ip} whitelisted`);
      onDone();
      onClose();
    },
    onError: (err) => {
      const known = knownLoginsFrom(err);
      if (known !== null) {
        // Server found sign-ins we didn't know about: ask explicitly.
        setKnownLogins(known);
        setConfirmed(false);
        return;
      }
      toast.error(apiErrorMessage(err) ?? "Action failed");
    },
  });

  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent className="sm:max-w-md">
        <DialogHeader>
          <DialogTitle>{isBlock ? `Block ${ip} permanently?` : `Whitelist ${ip}?`}</DialogTitle>
          <DialogDescription>
            {isBlock
              ? "Every request from this address is refused until an admin lifts the block."
              : "This address gets no further block proposals. Logging and rate limits keep working as usual."}
          </DialogDescription>
        </DialogHeader>

        {warnKnown && (
          <label className="flex items-start gap-2 rounded-md border border-amber-400/30 bg-amber-400/10 px-3 py-2 text-xs text-foreground">
            <input
              type="checkbox"
              checked={confirmed}
              onChange={(e) => setConfirmed(e.target.checked)}
              className="mt-0.5"
            />
            <span>
              {knownLogins !== null ? `${knownLogins} successful sign-in(s)` : "Successful sign-ins"} came from this
              address. I understand that blocking it locks out everyone working from it.
            </span>
          </label>
        )}

        <Input
          value={reason}
          onChange={(e) => setReason(e.target.value)}
          placeholder="Reason (optional, shown in the list and audit log)"
          maxLength={500}
          aria-label="Reason"
        />

        <div className="flex justify-end gap-2 pt-1">
          <Button variant="ghost" size="sm" onClick={onClose}>
            Cancel
          </Button>
          <Button
            size="sm"
            variant={isBlock ? "destructive" : "default"}
            disabled={submit.isPending || (warnKnown && !confirmed)}
            onClick={() => submit.mutate()}
          >
            {isBlock ? "Block" : "Whitelist"}
          </Button>
        </div>
      </DialogContent>
    </Dialog>
  );
}

function knownLoginsFrom(err: unknown): number | null {
  if (!(err instanceof ApiError) || err.status !== 409) return null;
  const body = err.body as { code?: string; knownLogins?: number } | null;
  return body?.code === "known_logins" ? (body.knownLogins ?? 0) : null;
}
