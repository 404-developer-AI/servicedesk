import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { AlertCircle, CheckCircle2, Copy, KeyRound, Search, Trash2 } from "lucide-react";
import { ApiError, telavoxAdminApi, type SettingEntry } from "@/lib/api";
import { formatDateTimeMedium } from "@/lib/dateFormat";
import { Button } from "@/components/ui/button";
import { SettingField } from "@/components/settings/SettingField";

const LOOKUP_QK = ["integrations", "telavox", "contact-lookup"] as const;

/// Settings → Integrations → Telavox → Contact lookup (v0.1.35). Telavox
/// calls our lookup URL on a ringing call and shows name + company in its
/// own app. Token generated here, shown once.
export function TelavoxContactLookupSection({
  enabledEntry,
  settingsQueryKey,
}: {
  enabledEntry: SettingEntry | undefined;
  settingsQueryKey: readonly unknown[];
}) {
  const qc = useQueryClient();
  const status = useQuery({ queryKey: LOOKUP_QK, queryFn: telavoxAdminApi.contactLookupStatus });
  const [freshToken, setFreshToken] = useState<string | null>(null);
  const [confirm, setConfirm] = useState<"rotate" | "delete" | null>(null);

  const configured = status.data?.tokenConfigured ?? false;
  const url = `${window.location.origin}${status.data?.path ?? "/api/public/telavox/contact-lookup"}?number={system.caller}`;

  const generate = useMutation({
    mutationFn: () => telavoxAdminApi.generateContactLookupToken(),
    onSuccess: (r) => {
      setFreshToken(r.token);
      setConfirm(null);
      toast.success(configured ? "New token generated — the old one no longer works" : "Token generated");
      qc.invalidateQueries({ queryKey: LOOKUP_QK });
    },
    onError: (err) =>
      toast.error(err instanceof ApiError ? `Could not generate the token (${err.status})` : "Could not generate the token"),
  });

  const remove = useMutation({
    mutationFn: () => telavoxAdminApi.deleteContactLookupToken(),
    onSuccess: () => {
      setFreshToken(null);
      setConfirm(null);
      toast.success("Token deleted — lookups are off");
      qc.invalidateQueries({ queryKey: LOOKUP_QK });
    },
  });

  const copy = async (value: string, label: string) => {
    try {
      await navigator.clipboard.writeText(value);
      toast.success(`${label} copied`);
    } catch {
      toast.error("Could not copy — select the value manually");
    }
  };

  const s = status.data;

  return (
    <section className="sd-telavox-lookup space-y-4 rounded-xl border border-glass-strong bg-glass p-5">
      <div className="flex items-center gap-2 text-sm font-medium text-foreground">
        <Search className="h-4 w-4 text-muted-foreground" />
        Contact lookup in the Telavox app
      </div>
      <p className="max-w-2xl text-xs text-muted-foreground/70">
        Telavox looks up a ringing caller here and shows the contact's name and
        company in its own desktop and mobile app. Only name and company are
        returned. Each Telavox user needs the Contact Lookup feature, plus a
        webhook and a call widget in the Telavox Admin Portal.
      </p>

      {enabledEntry ? (
        <SettingField
          entry={enabledEntry}
          queryKey={settingsQueryKey}
          label="Enabled"
          hint="Off, or no token generated: the lookup URL answers as if it doesn't exist."
        />
      ) : null}

      <div className="space-y-2">
        <div className="flex items-center gap-2 text-xs font-medium text-foreground">
          <KeyRound className="h-3.5 w-3.5 text-muted-foreground" />
          Token
        </div>
        <div className="flex flex-wrap items-center gap-2">
          {!configured ? (
            <Button size="sm" className="h-9" disabled={generate.isPending} onClick={() => generate.mutate()}>
              Generate token
            </Button>
          ) : confirm === null ? (
            <>
              <span className="inline-flex items-center gap-1 text-xs text-emerald-300">
                <CheckCircle2 className="h-3 w-3" /> Token set
              </span>
              <Button size="sm" variant="ghost" className="h-9" onClick={() => setConfirm("rotate")}>
                Generate new token
              </Button>
              <Button
                size="sm"
                variant="ghost"
                className="h-9 text-muted-foreground hover:text-foreground"
                onClick={() => setConfirm("delete")}
              >
                <Trash2 className="mr-1.5 h-3.5 w-3.5" />
                Delete
              </Button>
            </>
          ) : (
            <>
              <span className="text-xs text-amber-300">
                {confirm === "rotate"
                  ? "The current token stops working at once — update the Telavox webhook afterwards."
                  : "Lookups stop at once."}
              </span>
              <Button
                size="sm"
                className="h-9"
                disabled={generate.isPending || remove.isPending}
                onClick={() => (confirm === "rotate" ? generate.mutate() : remove.mutate())}
              >
                {confirm === "rotate" ? "Generate new token" : "Delete token"}
              </Button>
              <Button size="sm" variant="ghost" className="h-9" onClick={() => setConfirm(null)}>
                Cancel
              </Button>
            </>
          )}
          {!configured && (
            <span className="inline-flex items-center gap-1 text-xs text-muted-foreground/60">
              <AlertCircle className="h-3 w-3" /> No token yet
            </span>
          )}
        </div>
        {freshToken && (
          <div className="space-y-1.5 rounded-lg border border-amber-400/30 bg-amber-500/8 p-3">
            <p className="text-xs text-amber-200">
              Copy the token now — it is shown only once. Use it as the
              Authorization header of the Telavox webhook.
            </p>
            <div className="flex items-center gap-2">
              <code className="min-w-0 flex-1 break-all rounded bg-glass px-2 py-1 font-mono text-xs text-foreground">
                Bearer {freshToken}
              </code>
              <Button size="sm" variant="ghost" className="h-8" onClick={() => copy(`Bearer ${freshToken}`, "Header value")}>
                <Copy className="h-3.5 w-3.5" />
              </Button>
            </div>
          </div>
        )}
      </div>

      <div className="space-y-1.5">
        <div className="text-xs font-medium text-foreground">Telavox webhook</div>
        <dl className="grid grid-cols-[max-content_1fr] items-center gap-x-4 gap-y-1.5 text-xs">
          <dt className="text-muted-foreground">Event</dt>
          <dd className="text-foreground">Ringing</dd>
          <dt className="text-muted-foreground">Method</dt>
          <dd className="text-foreground">GET</dd>
          <dt className="text-muted-foreground">URL</dt>
          <dd className="flex min-w-0 items-center gap-2">
            <code className="min-w-0 break-all font-mono text-foreground">{url}</code>
            <Button size="sm" variant="ghost" className="h-7 shrink-0" onClick={() => copy(url, "URL")}>
              <Copy className="h-3.5 w-3.5" />
            </Button>
          </dd>
          <dt className="text-muted-foreground">Header</dt>
          <dd className="font-mono text-foreground">Authorization: Bearer &lt;token&gt;</dd>
          <dt className="text-muted-foreground">JSONPath</dt>
          <dd className="text-foreground">
            <code className="font-mono">label</code>
            <span className="text-muted-foreground"> — name · company in one field</span>
          </dd>
        </dl>
      </div>

      {s && (
        <p className="text-xs text-muted-foreground/70">
          Since the last restart: {s.found} found · {s.notFound} unknown number · {s.denied} refused
          {s.lastLookupUtc ? ` · last lookup ${formatDateTimeMedium(s.lastLookupUtc)}` : ""}
        </p>
      )}
    </section>
  );
}
