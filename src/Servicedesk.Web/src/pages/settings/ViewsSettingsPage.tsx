import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Inbox } from "lucide-react";
import { settingsApi } from "@/lib/settings-api";
import { ViewsPage } from "@/pages/views/ViewsPage";
import { ToggleSwitch } from "@/components/settings/ToggleSwitch";

export function ViewsSettingsPage() {
  const qc = useQueryClient();

  const { data: nav, isLoading } = useQuery({
    queryKey: ["settings", "navigation"],
    queryFn: settingsApi.navigation,
  });

  const toggle = useMutation({
    mutationFn: (value: boolean) =>
      settingsApi.update("Navigation.ShowOpenTickets", String(value)),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["settings", "navigation"] });
      toast.success("Navigation updated");
    },
    onError: () => {
      toast.error("Failed to update setting");
    },
  });

  return (
    <div className="flex flex-col gap-6">
      {/* System navigation section */}
      <div className="rounded-lg border border-glass-strong bg-glass p-4">
        <h2 className="text-xs font-medium uppercase tracking-widest text-muted-foreground/60 mb-3">
          Sidebar navigation
        </h2>
        <div className="flex items-center justify-between gap-4">
          <div className="flex items-center gap-3">
            <div className="flex h-8 w-8 items-center justify-center rounded-lg bg-primary/10 border border-primary/20">
              <Inbox className="h-4 w-4 text-primary/60" />
            </div>
            <div>
              <p className="text-sm font-medium text-foreground">Open Tickets</p>
              <p className="text-xs text-muted-foreground">
                Show the Open Tickets link in the sidebar for agents
              </p>
            </div>
          </div>
          <ToggleSwitch
            checked={nav?.showOpenTickets ?? true}
            disabled={isLoading || toggle.isPending}
            onChange={(v) => toggle.mutate(v)}
          />
        </div>
      </div>

      {/* Existing views management */}
      <ViewsPage />
    </div>
  );
}
