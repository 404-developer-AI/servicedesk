import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Inbox } from "lucide-react";
import { settingsApi } from "@/lib/api";
import { ViewsPage } from "@/pages/views/ViewsPage";
import { cn } from "@/lib/utils";

function ToggleSwitch({
  checked,
  disabled,
  onChange,
}: {
  checked: boolean;
  disabled?: boolean;
  onChange: (v: boolean) => void;
}) {
  return (
    <button
      type="button"
      role="switch"
      aria-checked={checked}
      disabled={disabled}
      onClick={() => onChange(!checked)}
      className={cn(
        "relative inline-flex h-6 w-11 shrink-0 cursor-pointer rounded-full border-2 border-transparent transition-colors duration-200 ease-in-out",
        "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background",
        "disabled:cursor-not-allowed disabled:opacity-50",
        checked
          ? "bg-gradient-to-r from-violet-600 to-indigo-600"
          : "bg-white/[0.08]",
      )}
    >
      <span
        className={cn(
          "pointer-events-none inline-block h-5 w-5 rounded-full bg-white shadow-lg ring-0 transition-transform duration-200 ease-in-out",
          checked ? "translate-x-5" : "translate-x-0",
        )}
      />
    </button>
  );
}

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
      <div className="rounded-lg border border-white/[0.06] bg-white/[0.02] p-4">
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
