import { useEffect, useState } from "react";
import { Link } from "@tanstack/react-router";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import {
  ArrowLeft,
  CheckCircle2,
  Clock,
  Globe,
  KeyRound,
  RefreshCw,
  Server,
  Trash2,
} from "lucide-react";
import {
  ApiError,
  apiErrorMessage,
  trmmAdminApi,
  type TrmmConnectionState,
} from "@/lib/api";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { Switch } from "@/components/ui/switch";
import { IntegrationAuditLog } from "@/components/integrations/IntegrationAuditLog";
import { TrmmClientMappingsSection } from "./trmm/TrmmClientMappingsSection";
import { cn } from "@/lib/utils";

const STATUS_QK = ["integrations", "trmm", "status"] as const;
const SECRET_QK = ["integrations", "trmm", "secret"] as const;

const STATE_LABEL: Record<
  TrmmConnectionState,
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

function formatWhen(iso: string | null): string {
  if (!iso) return "never";
  const d = new Date(iso);
  return d.toLocaleString();
}

export function TrmmIntegrationPage() {
  const qc = useQueryClient();
  const status = useQuery({ queryKey: STATUS_QK, queryFn: trmmAdminApi.status });
  const secret = useQuery({ queryKey: SECRET_QK, queryFn: trmmAdminApi.secretStatus });

  const [keyDraft, setKeyDraft] = useState("");
  const [urlDraft, setUrlDraft] = useState("");
  const [intervalDraft, setIntervalDraft] = useState<number>(15);

  useEffect(() => {
    if (status.data && intervalDraft === 15 && status.data.syncIntervalMinutes !== 15) {
      setIntervalDraft(status.data.syncIntervalMinutes);
    }
  }, [status.data, intervalDraft]);

  const hasKey = secret.data?.configured ?? false;
  const baseUrl = status.data?.baseUrl ?? "";
  const enabled = status.data?.enabled ?? false;
  const state = status.data?.state ?? "disabled";
  const stateMeta = STATE_LABEL[state];

  const saveKey = useMutation({
    mutationFn: () => trmmAdminApi.setSecret(keyDraft),
    onSuccess: () => {
      toast.success("API key saved");
      setKeyDraft("");
      qc.invalidateQueries({ queryKey: SECRET_QK });
      qc.invalidateQueries({ queryKey: STATUS_QK });
    },
    onError: (err) => {
      const upstream = apiErrorMessage(err);
      toast.error(upstream ?? (err instanceof ApiError ? `Save failed (${err.status})` : "Save failed"));
    },
  });

  const deleteKey = useMutation({
    mutationFn: () => trmmAdminApi.deleteSecret(),
    onSuccess: () => {
      toast.success("API key cleared");
      qc.invalidateQueries({ queryKey: SECRET_QK });
      qc.invalidateQueries({ queryKey: STATUS_QK });
    },
  });

  const saveBaseUrl = useMutation({
    mutationFn: () => trmmAdminApi.setBaseUrl(urlDraft.trim()),
    onSuccess: () => {
      toast.success("Base URL saved");
      setUrlDraft("");
      qc.invalidateQueries({ queryKey: STATUS_QK });
    },
    onError: (err) => {
      const upstream = apiErrorMessage(err);
      toast.error(upstream ?? "Save failed");
    },
  });

  const toggleEnabled = useMutation({
    mutationFn: (next: boolean) => trmmAdminApi.setEnabled(next),
    onSuccess: (_data, next) => {
      toast.success(next ? "Integration enabled" : "Integration disabled");
      qc.invalidateQueries({ queryKey: STATUS_QK });
    },
  });

  const saveInterval = useMutation({
    mutationFn: () => trmmAdminApi.setSyncInterval(intervalDraft),
    onSuccess: () => {
      toast.success(`Sync interval saved (every ${intervalDraft} min)`);
      qc.invalidateQueries({ queryKey: STATUS_QK });
    },
    onError: (err) => {
      const upstream = apiErrorMessage(err);
      toast.error(upstream ?? "Save failed");
    },
  });

  const test = useMutation({
    mutationFn: () => trmmAdminApi.testConnection(),
    onSuccess: (result) => {
      if (result.success) {
        toast.success(`Connected — ${result.clientCount ?? 0} clients, ${result.latencyMs} ms`);
      } else {
        toast.error(result.message ?? "Test failed");
      }
    },
    onError: (err) => {
      const upstream = apiErrorMessage(err);
      toast.error(upstream ?? "Test failed");
    },
  });

  const triggerSync = useMutation({
    mutationFn: () => trmmAdminApi.triggerSync(),
    onSuccess: (result) => {
      if (result.success) {
        toast.success(
          `Synced ${result.agents} agents from ${result.clients} clients (${result.latencyMs} ms)`,
        );
        qc.invalidateQueries({ queryKey: STATUS_QK });
        qc.invalidateQueries({ queryKey: ["assets"] });
        qc.invalidateQueries({ queryKey: ["integrations", "trmm", "client-mappings"] });
      } else {
        toast.error(result.errorMessage ?? "Sync failed");
      }
    },
  });

  const canTest = hasKey && (baseUrl?.length ?? 0) > 0 && !test.isPending;
  const canSync = enabled && canTest && !triggerSync.isPending;

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
            <Server className="h-6 w-6" />
          </div>
          <h1 className="text-display-md font-semibold text-foreground">Tactical RMM</h1>
          <p className="max-w-xl text-sm text-muted-foreground">
            Mirrors clients, sites, and agents from one Tactical RMM install into
            Servicedesk so the Assets page can filter and sort offline. Client names of
            the form <code className="text-foreground">[CODE] Customer Name</code>{" "}
            auto-link to companies via their <code className="text-foreground">code</code>.
          </p>
        </div>
        <Badge className={cn("border text-xs font-normal", stateMeta.tone)}>
          <span className={cn("mr-1.5 inline-block h-1.5 w-1.5 rounded-full", stateMeta.dot)} />
          {stateMeta.text}
        </Badge>
      </header>

      <section className="rounded-lg border border-glass bg-glass p-5">
        <div className="flex items-center justify-between gap-4">
          <div>
            <h2 className="text-sm font-medium text-foreground">Enabled</h2>
            <p className="text-xs text-muted-foreground">
              When off, the background sync sleeps and the manual Sync button is gated.
            </p>
          </div>
          <Switch
            checked={enabled}
            disabled={toggleEnabled.isPending}
            onCheckedChange={(v) => toggleEnabled.mutate(v)}
            aria-label="Enable Tactical RMM integration"
          />
        </div>
      </section>

      <section className="rounded-lg border border-glass bg-glass p-5 space-y-4">
        <div className="flex items-center gap-2 text-sm font-medium text-foreground">
          <Globe className="h-4 w-4 text-primary" /> Base URL
        </div>
        {status.isLoading ? (
          <Skeleton className="h-10 w-full" />
        ) : (
          <div className="flex gap-2">
            <Input
              placeholder={baseUrl || "https://api.trmm.example.com"}
              value={urlDraft}
              onChange={(e) => setUrlDraft(e.target.value)}
            />
            <Button
              onClick={() => saveBaseUrl.mutate()}
              disabled={urlDraft.trim().length === 0 || saveBaseUrl.isPending}
            >
              Save
            </Button>
          </div>
        )}
        {baseUrl && <p className="text-xs text-muted-foreground">Current: {baseUrl}</p>}
      </section>

      <section className="rounded-lg border border-glass bg-glass p-5 space-y-4">
        <div className="flex items-center justify-between">
          <div className="flex items-center gap-2 text-sm font-medium text-foreground">
            <KeyRound className="h-4 w-4 text-primary" /> API key
          </div>
          {hasKey && (
            <Button
              variant="ghost"
              size="sm"
              onClick={() => deleteKey.mutate()}
              disabled={deleteKey.isPending}
              className="text-destructive hover:text-destructive"
            >
              <Trash2 className="mr-1 h-3 w-3" /> Clear
            </Button>
          )}
        </div>
        <div className="flex gap-2">
          <Input
            type="password"
            placeholder={hasKey ? "API key configured — paste a new one to replace" : "Paste TRMM API key"}
            value={keyDraft}
            onChange={(e) => setKeyDraft(e.target.value)}
          />
          <Button
            onClick={() => saveKey.mutate()}
            disabled={keyDraft.trim().length === 0 || saveKey.isPending}
          >
            Save
          </Button>
        </div>
        <p className="text-xs text-muted-foreground">
          The key is encrypted at rest via DataProtection and sent on every call via the{" "}
          <code className="text-foreground">X-API-KEY</code> header.
        </p>
      </section>

      <section className="rounded-lg border border-glass bg-glass p-5 space-y-4">
        <div className="flex items-center gap-2 text-sm font-medium text-foreground">
          <Clock className="h-4 w-4 text-primary" /> Sync interval
        </div>
        <div className="flex items-center gap-2">
          <Input
            type="number"
            min={1}
            max={1440}
            value={intervalDraft}
            onChange={(e) => setIntervalDraft(Number(e.target.value))}
            className="w-32"
          />
          <span className="text-sm text-muted-foreground">minutes</span>
          <Button
            onClick={() => saveInterval.mutate()}
            disabled={saveInterval.isPending || intervalDraft < 1 || intervalDraft > 1440}
          >
            Save
          </Button>
        </div>
        <p className="text-xs text-muted-foreground">
          The background worker reads this on every cycle, so changes take effect on the next tick.
        </p>
      </section>

      <section className="rounded-lg border border-glass bg-glass p-5 space-y-4">
        <div className="flex items-center justify-between">
          <div>
            <h2 className="text-sm font-medium text-foreground">Connection &amp; sync</h2>
            <p className="text-xs text-muted-foreground">
              Last sync: <strong className="text-foreground">{formatWhen(status.data?.lastSyncUtc ?? null)}</strong>
              {status.data?.lastStatus === "failed" && (
                <span className="ml-2 text-amber-400">— {status.data.lastError ?? "failed"}</span>
              )}
              {status.data?.lastStatus === "ok" && (
                <span className="ml-2 text-emerald-400">— ok</span>
              )}
            </p>
          </div>
          <div className="flex gap-2">
            <Button variant="outline" onClick={() => test.mutate()} disabled={!canTest}>
              {test.isPending ? (
                <>
                  <RefreshCw className="mr-2 h-3 w-3 animate-spin" /> Testing…
                </>
              ) : (
                <>
                  <CheckCircle2 className="mr-2 h-3 w-3" /> Test connection
                </>
              )}
            </Button>
            <Button onClick={() => triggerSync.mutate()} disabled={!canSync}>
              {triggerSync.isPending ? (
                <>
                  <RefreshCw className="mr-2 h-3 w-3 animate-spin" /> Syncing…
                </>
              ) : (
                <>
                  <RefreshCw className="mr-2 h-3 w-3" /> Sync now
                </>
              )}
            </Button>
          </div>
        </div>
      </section>

      <TrmmClientMappingsSection />

      <section className="rounded-lg border border-glass bg-glass p-5">
        <h2 className="mb-3 text-sm font-medium text-foreground">Integration audit log</h2>
        <IntegrationAuditLog integration="trmm" />
      </section>
    </div>
  );
}
