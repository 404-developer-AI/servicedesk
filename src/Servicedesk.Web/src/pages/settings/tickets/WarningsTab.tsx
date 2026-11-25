import { useMemo } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { settingsApi } from "@/lib/settings-api";
import { ToggleRow } from "../settingsShared";

/// Settings → Tickets → Warnings: the side-panel visual cues (moved out of
/// TicketsSettingsPage in v0.1.67; behaviour unchanged).
export function WarningsTab() {
  const qc = useQueryClient();
  const { data: entries, isLoading } = useQuery({
    queryKey: ["settings", "tickets-warnings"],
    queryFn: () => settingsApi.list("Tickets"),
    staleTime: 60_000,
  });

  const showContactNotLinked = useMemo(
    () =>
      entries?.find((e) => e.key === "Tickets.ShowContactNotLinkedWarning")?.value === "true",
    [entries],
  );

  const update = useMutation({
    mutationFn: ({ key, value }: { key: string; value: string }) =>
      settingsApi.update(key, value),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["settings", "tickets-warnings"] });
      qc.invalidateQueries({ queryKey: ["settings", "ticket-warnings"] });
      toast.success("Setting updated");
    },
    onError: () => toast.error("Could not update setting"),
  });

  return (
    <div className="space-y-6">
      <section className="glass-card p-5">
        <div className="space-y-1">
          <h2 className="text-sm font-medium uppercase tracking-wide text-muted-foreground">
            Side-panel warnings
          </h2>
          <p className="text-xs text-muted-foreground/70">
            Each toggle turns a single visual cue on or off in the ticket side panel.
          </p>
        </div>
        <div className="mt-4 space-y-3">
          <ToggleRow
            label="Contact not linked to a company"
            description="Pulses an amber pill at the top of the Status tab when the requester has no current company links."
            checked={!!showContactNotLinked}
            disabled={isLoading || update.isPending}
            onCheckedChange={(v) =>
              update.mutate({
                key: "Tickets.ShowContactNotLinkedWarning",
                value: v ? "true" : "false",
              })
            }
          />
        </div>
      </section>
    </div>
  );
}
