import { useEffect, useState } from "react";
import { Link } from "@tanstack/react-router";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import {
  ArrowLeft,
  BadgeCheck,
  Building2,
  CheckCircle2,
  Copy,
  Fingerprint,
  KeyRound,
  RefreshCw,
  ShieldCheck,
  Trash2,
} from "lucide-react";
import {
  ApiError,
  apiErrorMessage,
  m365AdminApi,
  type M365ConnectionState,
} from "@/lib/api";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { Switch } from "@/components/ui/switch";
import { IntegrationAuditLog } from "@/components/integrations/IntegrationAuditLog";
import microsoftLogo from "@/assets/integrations/microsoft.svg";
import { cn } from "@/lib/utils";

const STATUS_QK = ["integrations", "m365", "status"] as const;
const SECRET_QK = ["integrations", "m365", "secret"] as const;

const STATE_LABEL: Record<
  M365ConnectionState,
  { tone: string; text: string; dot: string }
> = {
  disabled: {
    tone: "border-glass-strong bg-glass-strong text-muted-foreground",
    text: "Disabled",
    dot: "bg-glass-strong",
  },
  "not-configured": {
    tone: "border-amber-400/30 bg-amber-500/[0.08] text-amber-200",
    text: "Not configured",
    dot: "bg-amber-400",
  },
  ready: {
    tone: "border-emerald-400/30 bg-emerald-500/10 text-emerald-300",
    text: "Ready",
    dot: "bg-emerald-400",
  },
};

/** Read-only value with a copy button — used for the redirect URI. */
function CopyField({ value, label }: { value: string; label: string }) {
  const copy = async () => {
    try {
      await navigator.clipboard.writeText(value);
      toast.success(`${label} copied`);
    } catch {
      toast.error("Could not copy to clipboard");
    }
  };
  return (
    <div className="flex items-stretch gap-2">
      <code className="flex-1 overflow-x-auto whitespace-nowrap rounded-md border border-glass-strong bg-black/20 px-3 py-2 font-mono text-xs text-foreground">
        {value}
      </code>
      <Button variant="outline" size="sm" onClick={copy} className="shrink-0">
        <Copy className="mr-1.5 h-3 w-3" /> Copy
      </Button>
    </div>
  );
}

export function M365IntegrationPage() {
  const qc = useQueryClient();
  const status = useQuery({ queryKey: STATUS_QK, queryFn: m365AdminApi.status });
  const secret = useQuery({ queryKey: SECRET_QK, queryFn: m365AdminApi.secretStatus });

  const [tenantDraft, setTenantDraft] = useState("");
  const [clientDraft, setClientDraft] = useState("");
  const [secretDraft, setSecretDraft] = useState("");
  const [seeded, setSeeded] = useState(false);

  // Seed the editable identifiers once from the server, so an admin sees the
  // current values and edits in place rather than from blank.
  useEffect(() => {
    if (!seeded && status.data) {
      setTenantDraft(status.data.tenantId ?? "");
      setClientDraft(status.data.clientId ?? "");
      setSeeded(true);
    }
  }, [seeded, status.data]);

  const hasSecret = secret.data?.configured ?? false;
  const enabled = status.data?.enabled ?? false;
  const state = status.data?.state ?? "disabled";
  const stateMeta = STATE_LABEL[state];
  const redirectUri = status.data?.redirectUri ?? "";
  const permissions = status.data?.requiredPermissions ?? [];

  const onErr = (err: unknown) => {
    const upstream = apiErrorMessage(err);
    toast.error(
      upstream ?? (err instanceof ApiError ? `Failed (${err.status})` : "Failed"),
    );
  };

  const saveConfig = useMutation({
    mutationFn: () => m365AdminApi.setConfig(tenantDraft.trim(), clientDraft.trim()),
    onSuccess: () => {
      toast.success("Tenant ID and client ID saved");
      qc.invalidateQueries({ queryKey: STATUS_QK });
    },
    onError: onErr,
  });

  const saveSecret = useMutation({
    mutationFn: () => m365AdminApi.setSecret(secretDraft),
    onSuccess: () => {
      toast.success("Client secret saved");
      setSecretDraft("");
      qc.invalidateQueries({ queryKey: SECRET_QK });
      qc.invalidateQueries({ queryKey: STATUS_QK });
    },
    onError: onErr,
  });

  const deleteSecret = useMutation({
    mutationFn: () => m365AdminApi.deleteSecret(),
    onSuccess: () => {
      toast.success("Client secret cleared");
      qc.invalidateQueries({ queryKey: SECRET_QK });
      qc.invalidateQueries({ queryKey: STATUS_QK });
    },
    onError: onErr,
  });

  const toggleEnabled = useMutation({
    mutationFn: (next: boolean) => m365AdminApi.setEnabled(next),
    onSuccess: (_d, next) => {
      toast.success(next ? "Integration enabled" : "Integration disabled");
      qc.invalidateQueries({ queryKey: STATUS_QK });
    },
    onError: onErr,
  });

  const test = useMutation({
    mutationFn: () => m365AdminApi.testConnection(),
    onSuccess: (result) => {
      if (result.success) {
        toast.success(`Credentials valid — token acquired in ${result.latencyMs} ms`);
      } else {
        toast.error(result.message ?? "Test failed");
      }
    },
    onError: onErr,
  });

  const configDirty =
    tenantDraft.trim() !== (status.data?.tenantId ?? "") ||
    clientDraft.trim() !== (status.data?.clientId ?? "");
  const canSaveConfig =
    tenantDraft.trim().length > 0 &&
    clientDraft.trim().length > 0 &&
    configDirty &&
    !saveConfig.isPending;
  const canTest =
    (status.data?.tenantId?.length ?? 0) > 0 &&
    (status.data?.clientId?.length ?? 0) > 0 &&
    hasSecret &&
    !test.isPending;

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
          <div className="mt-2 mb-2 flex h-10 w-10 items-center justify-center rounded-lg border border-glass-strong bg-glass">
            <img src={microsoftLogo} alt="" aria-hidden className="h-5 w-5" />
          </div>
          <h1 className="text-display-md font-semibold text-foreground">Microsoft 365</h1>
          <p className="max-w-2xl text-sm text-muted-foreground">
            One <strong>multi-tenant</strong> app registration in your own (MSP) tenant lets
            you read each customer's Microsoft 365 directory — users, assigned licenses and
            real mailbox types — app-only, after their admin grants consent once. Configure
            the shared app credentials here; the per-customer{" "}
            <strong>Connect with M365</strong> button lives on each company under{" "}
            <strong>Contracts → Microsoft 365 matching</strong>.
          </p>
        </div>
        <Badge className={cn("border text-xs font-normal", stateMeta.tone)}>
          <span className={cn("mr-1.5 inline-block h-1.5 w-1.5 rounded-full", stateMeta.dot)} />
          {stateMeta.text}
        </Badge>
      </header>

      {/* Enabled toggle */}
      <section className="rounded-lg border border-glass bg-glass p-5">
        <div className="flex items-center justify-between gap-4">
          <div>
            <h2 className="text-sm font-medium text-foreground">Enabled</h2>
            <p className="text-xs text-muted-foreground">
              Master switch for the customer-tenant reader. When off, no customer directories
              are queried even where consent already exists.
            </p>
          </div>
          <Switch
            checked={enabled}
            disabled={toggleEnabled.isPending}
            onCheckedChange={(v) => toggleEnabled.mutate(v)}
            aria-label="Enable Microsoft 365 integration"
          />
        </div>
      </section>

      {/* App credentials */}
      <section className="rounded-lg border border-glass bg-glass p-5 space-y-5">
        <div className="flex items-center gap-2 text-sm font-medium text-foreground">
          <Fingerprint className="h-4 w-4 text-primary" /> MSP app credentials
        </div>
        <p className="-mt-2 text-xs text-muted-foreground">
          The three values from the app registration in <strong>your</strong> tenant. The
          tenant ID and client ID are identifiers; the client secret is encrypted at rest via
          DataProtection and never returned to the browser.
        </p>

        {status.isLoading ? (
          <Skeleton className="h-32 w-full" />
        ) : (
          <>
            <div className="grid gap-4 sm:grid-cols-2">
              <label className="space-y-1.5">
                <span className="flex items-center gap-1.5 text-xs font-medium text-muted-foreground">
                  <Building2 className="h-3 w-3" /> Directory (tenant) ID
                </span>
                <Input
                  placeholder="00000000-0000-0000-0000-000000000000"
                  value={tenantDraft}
                  onChange={(e) => setTenantDraft(e.target.value)}
                  spellCheck={false}
                  autoComplete="off"
                />
                <span className="text-[11px] text-muted-foreground/70">
                  Your MSP tenant — a GUID or <code>name.onmicrosoft.com</code>.
                </span>
              </label>
              <label className="space-y-1.5">
                <span className="flex items-center gap-1.5 text-xs font-medium text-muted-foreground">
                  <BadgeCheck className="h-3 w-3" /> Application (client) ID
                </span>
                <Input
                  placeholder="00000000-0000-0000-0000-000000000000"
                  value={clientDraft}
                  onChange={(e) => setClientDraft(e.target.value)}
                  spellCheck={false}
                  autoComplete="off"
                />
                <span className="text-[11px] text-muted-foreground/70">
                  The app registration's client ID (a GUID).
                </span>
              </label>
            </div>
            <div className="flex justify-end">
              <Button onClick={() => saveConfig.mutate()} disabled={!canSaveConfig}>
                Save identifiers
              </Button>
            </div>

            <div className="border-t border-glass pt-5">
              <div className="flex items-center justify-between">
                <span className="flex items-center gap-1.5 text-xs font-medium text-muted-foreground">
                  <KeyRound className="h-3 w-3" /> Client secret
                </span>
                {hasSecret && (
                  <Button
                    variant="ghost"
                    size="sm"
                    onClick={() => deleteSecret.mutate()}
                    disabled={deleteSecret.isPending}
                    className="text-destructive hover:text-destructive"
                  >
                    <Trash2 className="mr-1 h-3 w-3" /> Clear
                  </Button>
                )}
              </div>
              <div className="mt-1.5 flex gap-2">
                <Input
                  type="password"
                  placeholder={
                    hasSecret
                      ? "Secret configured — paste a new one to replace"
                      : "Paste the secret Value (not the Secret ID)"
                  }
                  value={secretDraft}
                  onChange={(e) => setSecretDraft(e.target.value)}
                  spellCheck={false}
                  autoComplete="off"
                />
                <Button
                  onClick={() => saveSecret.mutate()}
                  disabled={secretDraft.trim().length === 0 || saveSecret.isPending}
                >
                  Save
                </Button>
              </div>
              <p className="mt-1.5 text-[11px] text-muted-foreground/70">
                From <strong>Certificates &amp; secrets</strong> in the app registration — copy
                the secret's <em>Value</em> the moment you create it.
              </p>
            </div>

            <div className="flex items-center justify-between border-t border-glass pt-5">
              <p className="text-xs text-muted-foreground">
                Verify the credentials by requesting a token against your MSP tenant. This does
                not require any customer consent.
              </p>
              <Button variant="outline" onClick={() => test.mutate()} disabled={!canTest}>
                {test.isPending ? (
                  <>
                    <RefreshCw className="mr-2 h-3 w-3 animate-spin" /> Testing…
                  </>
                ) : (
                  <>
                    <CheckCircle2 className="mr-2 h-3 w-3" /> Test credentials
                  </>
                )}
              </Button>
            </div>
          </>
        )}
      </section>

      {/* Entra ID app setup guidance */}
      <section className="rounded-lg border border-glass bg-glass p-5 space-y-5">
        <div className="flex items-center gap-2 text-sm font-medium text-foreground">
          <ShieldCheck className="h-4 w-4 text-primary" /> Entra ID app setup
        </div>
        <p className="-mt-2 text-xs text-muted-foreground">
          Register one app in your tenant, set it to <strong>multi-tenant</strong>{" "}
          (accounts in any organizational directory), then add the permissions and redirect
          URI below. Each customer admin consents once via the{" "}
          <strong>Connect with M365</strong> flow.
        </p>

        <div className="space-y-2">
          <span className="text-xs font-medium text-muted-foreground">
            Required API permissions — Microsoft Graph,{" "}
            <span className="text-foreground">Application</span> type (admin consent)
          </span>
          <ul className="divide-y divide-glass overflow-hidden rounded-md border border-glass-strong bg-black/10">
            {permissions.length === 0 ? (
              <li className="px-3 py-2 text-xs text-muted-foreground/60">Loading…</li>
            ) : (
              permissions.map((p) => (
                <li key={p.name} className="flex items-start gap-3 px-3 py-2.5">
                  <code className="shrink-0 rounded bg-primary/10 px-2 py-0.5 font-mono text-[11px] text-primary">
                    {p.name}
                  </code>
                  <span className="text-xs text-muted-foreground">{p.description}</span>
                </li>
              ))
            )}
          </ul>
          <p className="text-[11px] text-muted-foreground/70">
            All three are <strong>Application</strong> permissions (not Delegated) and require
            admin consent in each customer tenant.
          </p>
        </div>

        <div className="space-y-2">
          <span className="text-xs font-medium text-muted-foreground">
            Redirect URI — add under <strong>Authentication → Web</strong>
          </span>
          {redirectUri ? (
            <CopyField value={redirectUri} label="Redirect URI" />
          ) : (
            <Skeleton className="h-10 w-full" />
          )}
          <p className="text-[11px] text-muted-foreground/70">
            Where Microsoft returns the customer admin after they grant consent. It must match
            exactly. If your public URL is not yet set, configure it under{" "}
            <strong>Settings → App</strong> first.
          </p>
        </div>
      </section>

      {/* Audit log */}
      <section className="rounded-lg border border-glass bg-glass p-5">
        <h2 className="mb-3 text-sm font-medium text-foreground">Integration audit log</h2>
        <IntegrationAuditLog integration="m365" />
      </section>
    </div>
  );
}
