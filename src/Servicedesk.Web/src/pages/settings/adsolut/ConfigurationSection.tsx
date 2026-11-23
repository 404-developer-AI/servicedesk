import { useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Copy } from "lucide-react";
import { adsolutApi, type AdsolutStatus } from "@/lib/adsolut-api";
import type { SettingEntry } from "@/lib/settings-api";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { SettingField } from "@/components/settings/SettingField";
import { AdsolutScopesPicker } from "@/components/settings/AdsolutScopesPicker";
import { ADSOLUT_SETTINGS_QUERY_KEY, ADSOLUT_STATUS_QUERY_KEY, ADSOLUT_SECRET_QUERY_KEY } from "@/pages/settings/adsolut/shared";
import { findEntry } from "@/components/settings/settingEntries";
import { errorMessage } from "@/lib/http";

export function ConfigurationSection({
  status: s,
  settingsEntries,
  secretConfigured,
}: {
  status: AdsolutStatus;
  settingsEntries: SettingEntry[] | undefined;
  secretConfigured: boolean | undefined;
}) {
  const qc = useQueryClient();

  const [secretValue, setSecretValue] = useState("");

  const saveSecret = useMutation({
    mutationFn: () => adsolutApi.setSecret(secretValue),
    onSuccess: () => {
      toast.success("Client secret saved");
      setSecretValue("");
      qc.invalidateQueries({ queryKey: ADSOLUT_SECRET_QUERY_KEY });
      qc.invalidateQueries({ queryKey: ADSOLUT_STATUS_QUERY_KEY });
    },
    onError: (err) =>
      toast.error(errorMessage(err, "Save failed")),
  });

  const deleteSecret = useMutation({
    mutationFn: () => adsolutApi.deleteSecret(),
    onSuccess: () => {
      toast.success("Client secret cleared");
      qc.invalidateQueries({ queryKey: ADSOLUT_SECRET_QUERY_KEY });
      qc.invalidateQueries({ queryKey: ADSOLUT_STATUS_QUERY_KEY });
    },
  });

  const copyRedirect = async (url: string) => {
    try {
      await navigator.clipboard.writeText(url);
      toast.success("Redirect URI copied");
    } catch {
      toast.error("Could not copy — copy it manually");
    }
  };

  return (
    <section className="rounded-lg border border-glass-strong bg-glass p-5">
      <header className="mb-4 space-y-1">
        <h2 className="text-xs font-medium uppercase tracking-widest text-muted-foreground/60">
          Configuration
        </h2>
        <p className="text-xs text-muted-foreground">
          Wolters Kluwer provisions a client_id + secret per servicedesk install; the
          redirect URI below must match exactly what was registered. Switching environment
          requires a disconnect + reconnect — refresh tokens issued by UAT do not work
          against production.
        </p>
      </header>

      <div className="space-y-3">
        {(["Adsolut.Environment", "Adsolut.ClientId"] as const).map((key) => {
          const entry = findEntry(settingsEntries, key);
          if (!entry) return null;
          return (
            <SettingField
              key={key}
              entry={entry}
              queryKey={ADSOLUT_SETTINGS_QUERY_KEY}
            />
          );
        })}

        <AdsolutScopesPicker
          entry={findEntry(settingsEntries, "Adsolut.Scopes")}
          queryKey={ADSOLUT_SETTINGS_QUERY_KEY}
          needsReconnect={s.scopesNeedReconnect}
        />

        <div className="flex items-center justify-between gap-3 rounded-md border border-glass-strong bg-glass px-3 py-2">
          <div className="min-w-0 flex-1">
            <div className="text-sm">Client secret</div>
            <div className="text-xs text-muted-foreground">
              {secretConfigured ? "Configured (encrypted at rest)" : "Not configured"}
            </div>
          </div>
          <Input
            type="password"
            placeholder={secretConfigured ? "••••••• (enter to replace)" : "Paste secret"}
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
          {secretConfigured && (
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

        <div className="flex items-center justify-between gap-3 rounded-md border border-glass-strong bg-glass px-3 py-2">
          <div className="min-w-0 flex-1">
            <div className="text-sm">Redirect URI</div>
            <div className="text-xs text-muted-foreground">
              Register this exact value in your Wolters Kluwer client. Auto-derived from{" "}
              <span className="font-mono">App.PublicBaseUrl</span>.
            </div>
          </div>
          <code className="truncate rounded bg-glass px-2 py-1 font-mono text-xs">
            {s.redirectUri}
          </code>
          <Button
            size="sm"
            variant="ghost"
            onClick={() => copyRedirect(s.redirectUri)}
            aria-label="Copy redirect URI"
          >
            <Copy className="h-3.5 w-3.5" />
          </Button>
        </div>
      </div>
    </section>
  );
}
