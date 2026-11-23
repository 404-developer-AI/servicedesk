import * as React from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { DatabaseBackup } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { Switch } from "@/components/ui/switch";
import { timesheetImportAdminApi } from "@/lib/reporting-admin-api";
import { errorMessage } from "@/lib/http";
import { SectionHeader } from "./timesheetSettingsShared";

// ---- Migration import section ----------------------------------------

const IMPORT_STATUS_KEY = ["settings", "timesheet", "import", "status"] as const;

function generateToken(): string {
  const bytes = new Uint8Array(30);
  crypto.getRandomValues(bytes);
  const chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
  return Array.from(bytes, (b) => chars[b % chars.length]).join("");
}

export function MigrationImportSection() {
  const qc = useQueryClient();
  const [showInput, setShowInput] = React.useState(false);
  const [draft, setDraft] = React.useState("");

  const status = useQuery({
    queryKey: IMPORT_STATUS_KEY,
    queryFn: () => timesheetImportAdminApi.status(),
  });

  const setEnabled = useMutation({
    mutationFn: (enabled: boolean) => timesheetImportAdminApi.setEnabled(enabled),
    onSuccess: (_, enabled) => {
      qc.invalidateQueries({ queryKey: IMPORT_STATUS_KEY });
      toast.success(enabled ? "Migration import enabled" : "Migration import disabled");
    },
    onError: (err) => toast.error(errorMessage(err, "Failed to update")),
  });

  const setSecret = useMutation({
    mutationFn: (value: string) => timesheetImportAdminApi.setSecret(value),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: IMPORT_STATUS_KEY });
      toast.success("Import token saved");
      setDraft("");
      setShowInput(false);
    },
    onError: (err) => toast.error(errorMessage(err, "Failed to save token")),
  });

  const deleteSecret = useMutation({
    mutationFn: () => timesheetImportAdminApi.deleteSecret(),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: IMPORT_STATUS_KEY });
      toast.success("Import token cleared");
    },
    onError: (err) => toast.error(errorMessage(err, "Failed to clear token")),
  });

  const configured = status.data?.secretConfigured ?? false;
  const showDraftInput = showInput || !configured;

  return (
    <section className="glass-card p-6">
      <SectionHeader
        icon={<DatabaseBackup className="h-5 w-5" />}
        title="Migration import"
        description="Pre-shared secret and master switch for the standalone migration tool that bulk-imports historical timesheet rows. The surface is only live when the toggle is on AND a token is configured."
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
              <p className="text-sm font-medium text-foreground">Enable import surface</p>
              <p className="mt-0.5 text-xs text-muted-foreground">
                When off, the migration endpoint rejects all requests regardless of the token.
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
              <p className="text-sm font-medium text-foreground">Import token</p>
              {configured && !showInput && (
                <>
                  <Badge className="border border-emerald-400/20 bg-emerald-400/10 text-[10px] font-normal text-emerald-200">
                    Token configured
                  </Badge>
                  <Button
                    size="sm"
                    variant="ghost"
                    className="h-7 px-2 text-xs"
                    onClick={() => { setShowInput(true); setDraft(""); }}
                  >
                    Replace token
                  </Button>
                  <Button
                    size="sm"
                    variant="ghost"
                    className="h-7 px-2 text-xs text-destructive hover:text-destructive"
                    disabled={deleteSecret.isPending}
                    onClick={() => deleteSecret.mutate()}
                  >
                    Clear token
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
                    placeholder="Paste or generate a token…"
                    className="h-9 flex-1 font-mono text-sm"
                    disabled={setSecret.isPending}
                  />
                  <Button
                    size="sm"
                    variant="ghost"
                    className="h-9 shrink-0 px-3"
                    onClick={() => setDraft(generateToken())}
                    disabled={setSecret.isPending}
                  >
                    Generate
                  </Button>
                  <Button
                    size="sm"
                    className="h-9 shrink-0 px-3"
                    disabled={draft.length < 24 || setSecret.isPending}
                    onClick={() => setSecret.mutate(draft)}
                  >
                    Save token
                  </Button>
                  {showInput && configured && (
                    <Button
                      size="sm"
                      variant="ghost"
                      className="h-9 shrink-0 px-2 text-xs text-muted-foreground"
                      onClick={() => { setShowInput(false); setDraft(""); }}
                      disabled={setSecret.isPending}
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
