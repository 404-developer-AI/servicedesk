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

  if (graph.isLoading || secret.isLoading) {
    return <Skeleton className="h-48 w-full" />;
  }

  return (
    <section className="rounded-lg border border-white/[0.06] bg-white/[0.02] p-5">
      <header className="mb-4 flex items-start justify-between gap-4">
        <div className="space-y-1">
          <h2 className="text-xs font-medium uppercase tracking-widest text-muted-foreground/60">
            Microsoft Graph
          </h2>
          <p className="text-xs text-muted-foreground">
            App-registration credentials for mailbox polling. See{" "}
            <code className="rounded bg-white/[0.04] px-1">docs/microsoft-graph-setup.md</code>{" "}
            for the Azure Portal steps and required permissions.
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
      </div>
    </section>
  );
}
