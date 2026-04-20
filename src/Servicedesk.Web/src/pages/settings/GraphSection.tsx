import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { ApiError, graphAdminApi, settingsApi, type SettingEntry } from "@/lib/api";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { SettingField } from "@/components/settings/SettingField";

const GRAPH_QUERY_KEY = ["settings", "list", "Graph"] as const;
const GRAPH_SECRET_QUERY_KEY = ["settings", "graph", "secret"] as const;
const AUTH_QUERY_KEY = ["settings", "list", "Auth"] as const;
const APP_QUERY_KEY = ["settings", "list", "App"] as const;

function findEntry(entries: SettingEntry[] | undefined, key: string) {
  return entries?.find((e) => e.key === key);
}

export function GraphSection() {
  const qc = useQueryClient();
  const graph = useQuery({
    queryKey: GRAPH_QUERY_KEY,
    queryFn: () => settingsApi.list("Graph"),
  });
  const secret = useQuery({
    queryKey: GRAPH_SECRET_QUERY_KEY,
    queryFn: () => graphAdminApi.secretStatus(),
  });
  const auth = useQuery({
    queryKey: AUTH_QUERY_KEY,
    queryFn: () => settingsApi.list("Auth"),
  });
  const appSettings = useQuery({
    queryKey: APP_QUERY_KEY,
    queryFn: () => settingsApi.list("App"),
  });

  const [secretValue, setSecretValue] = useState("");
  const [testMailbox, setTestMailbox] = useState("");
  const [testResult, setTestResult] = useState<string | null>(null);

  const saveSecret = useMutation({
    mutationFn: async () => graphAdminApi.setSecret(secretValue),
    onSuccess: () => {
      toast.success("Client secret saved");
      setSecretValue("");
      qc.invalidateQueries({ queryKey: GRAPH_SECRET_QUERY_KEY });
    },
    onError: (err) =>
      toast.error(err instanceof ApiError ? `Save failed (${err.status})` : "Save failed"),
  });

  const deleteSecret = useMutation({
    mutationFn: async () => graphAdminApi.deleteSecret(),
    onSuccess: () => {
      toast.success("Client secret removed");
      qc.invalidateQueries({ queryKey: GRAPH_SECRET_QUERY_KEY });
    },
  });

  const runTest = useMutation({
    mutationFn: async () => graphAdminApi.test(testMailbox.trim()),
    onSuccess: (r) => {
      setTestResult(
        r.ok
          ? `OK — ${r.latencyMs ?? "?"}ms`
          : `FAILED — ${r.error ?? "unknown error"}`,
      );
    },
    onError: (err) =>
      setTestResult(err instanceof ApiError ? `FAILED — HTTP ${err.status}` : "FAILED"),
  });

  if (graph.isLoading || secret.isLoading || auth.isLoading || appSettings.isLoading) {
    return <Skeleton className="h-48 w-full" />;
  }

  const microsoftEnabledEntry = findEntry(auth.data, "Auth.Microsoft.Enabled");
  const microsoftEnabled = microsoftEnabledEntry?.value === "true";
  const publicBaseEntry = findEntry(appSettings.data, "App.PublicBaseUrl");
  const publicBaseSet = (publicBaseEntry?.value ?? "").trim().length > 0;
  const isFullyConfigured =
    secret.data?.configured === true &&
    (findEntry(graph.data, "Graph.TenantId")?.value ?? "").length > 0 &&
    (findEntry(graph.data, "Graph.ClientId")?.value ?? "").length > 0;

  return (
    <section className="rounded-lg border border-white/[0.06] bg-white/[0.02] p-5">
      <header className="mb-4 flex items-start justify-between gap-4">
        <div className="space-y-1">
          <h2 className="text-xs font-medium uppercase tracking-widest text-muted-foreground/60">
            Microsoft Graph
          </h2>
          <p className="text-xs text-muted-foreground">
            App-registration credentials for mailbox polling, outbound mail, and
            Microsoft 365 sign-in. One app covers all three. See{" "}
            <code className="rounded bg-white/[0.04] px-1">docs/microsoft-graph-setup.md</code>{" "}
            for the Azure Portal walkthrough.
          </p>
          <p className="text-[11px] text-muted-foreground/80">
            Required permissions — application:{" "}
            <code className="rounded bg-white/[0.04] px-1">Mail.ReadWrite</code>,{" "}
            <code className="rounded bg-white/[0.04] px-1">Mail.Send</code>,{" "}
            <code className="rounded bg-white/[0.04] px-1">User.Read.All</code>. Delegated
            (only if M365 login is enabled):{" "}
            <code className="rounded bg-white/[0.04] px-1">openid</code>,{" "}
            <code className="rounded bg-white/[0.04] px-1">profile</code>,{" "}
            <code className="rounded bg-white/[0.04] px-1">email</code>,{" "}
            <code className="rounded bg-white/[0.04] px-1">User.Read</code>. Redirect URI:{" "}
            <code className="rounded bg-white/[0.04] px-1">&lt;public-host&gt;/api/auth/microsoft/callback</code>.
          </p>
        </div>
      </header>

      <div className="space-y-3">
        {["Graph.TenantId", "Graph.ClientId"].map((key) => {
          const entry = findEntry(graph.data, key);
          if (!entry) return null;
          return <SettingField key={key} entry={entry} queryKey={GRAPH_QUERY_KEY} />;
        })}

        <div className="flex items-center justify-between gap-3 rounded-md border border-white/[0.06] bg-white/[0.02] px-3 py-2">
          <div className="min-w-0 flex-1">
            <div className="text-sm">Client secret</div>
            <div className="text-xs text-muted-foreground">
              {secret.data?.configured ? "Configured (encrypted at rest)" : "Not configured"}
            </div>
          </div>
          <Input
            type="password"
            placeholder={secret.data?.configured ? "••••••• (enter to replace)" : "Paste secret"}
            value={secretValue}
            onChange={(e) => setSecretValue(e.target.value)}
            className="max-w-xs"
          />
          <Button
            size="sm"
            onClick={() => saveSecret.mutate()}
            disabled={!secretValue || saveSecret.isPending}
          >
            {saveSecret.isPending ? "Saving…" : "Save"}
          </Button>
          {secret.data?.configured && (
            <Button
              size="sm"
              variant="ghost"
              onClick={() => deleteSecret.mutate()}
              disabled={deleteSecret.isPending}
            >
              Clear
            </Button>
          )}
        </div>

        <div className="flex items-center gap-3 rounded-md border border-white/[0.06] bg-white/[0.02] px-3 py-2">
          <div className="shrink-0 text-sm">Test connection</div>
          <Input
            type="email"
            placeholder="mailbox@company.com"
            value={testMailbox}
            onChange={(e) => setTestMailbox(e.target.value)}
            className="max-w-xs"
          />
          <Button
            size="sm"
            onClick={() => runTest.mutate()}
            disabled={!testMailbox || runTest.isPending}
          >
            {runTest.isPending ? "Testing…" : "Test"}
          </Button>
          {testResult && (
            <span
              className={
                testResult.startsWith("OK")
                  ? "text-xs text-emerald-400"
                  : "text-xs text-red-400"
              }
            >
              {testResult}
            </span>
          )}
        </div>

        <div className="mt-2 border-t border-white/[0.04] pt-4">
          <h3 className="text-xs font-medium uppercase tracking-widest text-muted-foreground/60 mb-2">
            Microsoft 365 sign-in
          </h3>
          <p className="text-xs text-muted-foreground mb-3">
            When on, the login page shows a "Sign in with Microsoft" button and
            the callback endpoint is active. The app registration must carry
            the delegated <code className="rounded bg-white/[0.04] px-1">openid / profile / email / User.Read</code>{" "}
            permissions and have a redirect URI pointing at{" "}
            <code className="rounded bg-white/[0.04] px-1">&lt;App.PublicBaseUrl&gt;/api/auth/microsoft/callback</code>.
            See the setup guide for details.
          </p>

          {publicBaseEntry && (
            <div className="mb-3 space-y-1.5">
              <SettingField
                entry={publicBaseEntry}
                queryKey={APP_QUERY_KEY}
                label="App.PublicBaseUrl"
                hint="Browser-origin for this install. Production: the public HTTPS URL behind nginx (e.g. https://desk.example.com). Dev: the Vite dev-server origin (e.g. http://localhost:5173). Required so the callback redirect lands on the SPA, not the bare Kestrel port."
              />
              {microsoftEnabled && !publicBaseSet && (
                <div className="rounded-md border border-amber-500/30 bg-amber-500/[0.08] px-3 py-2 text-[11px] text-amber-200">
                  M365 login is on but <span className="font-mono">App.PublicBaseUrl</span>{" "}
                  is empty. After a successful Azure sign-in the browser will
                  land on a 404 — fill this in and match it with the redirect
                  URI in the Azure app registration.
                </div>
              )}
            </div>
          )}

          {!isFullyConfigured && (
            <div className="mb-3 rounded-md border border-amber-500/30 bg-amber-500/[0.08] px-3 py-2 text-[11px] text-amber-200">
              Fill in Tenant ID, Client ID and the client secret above before enabling.
            </div>
          )}
          {microsoftEnabledEntry ? (
            <SettingField
              entry={microsoftEnabledEntry}
              queryKey={AUTH_QUERY_KEY}
              label="Auth.Microsoft.Enabled"
              readOnly={!isFullyConfigured}
            />
          ) : (
            <div className="rounded-md border border-white/[0.06] bg-white/[0.02] px-3 py-2 text-xs text-muted-foreground">
              Setting row not yet seeded. Restart the API once to seed the new
              Auth defaults.
            </div>
          )}
        </div>
      </div>
    </section>
  );
}
