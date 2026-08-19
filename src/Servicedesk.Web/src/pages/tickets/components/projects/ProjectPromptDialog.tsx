import * as React from "react";
import { useQuery } from "@tanstack/react-query";
import { FolderKanban } from "lucide-react";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Button } from "@/components/ui/button";
import { cn } from "@/lib/utils";
import { ticketApi, type ProjectPromptItem, type ProjectSettings } from "@/lib/ticket-api";

export const projectSettingsKey = ["settings", "projects"] as const;

export function useProjectSettings() {
  return useQuery<ProjectSettings>({
    queryKey: projectSettingsKey,
    queryFn: ticketApi.projectSettings,
    staleTime: 60_000,
  });
}

/// v0.0.105 — first-open link prompt: the ticket's company has one or
/// more open project tickets and this ticket is not linked (and the
/// question was never answered on it). "No thanks" is remembered per
/// ticket server-side, so the prompt shows at most once.
export function ProjectPromptDialog({
  open,
  projects,
  linking,
  onLink,
  onDecline,
}: {
  open: boolean;
  projects: ProjectPromptItem[];
  linking: boolean;
  onLink: (projectTicketId: string) => void;
  onDecline: () => void;
}) {
  const [selectedId, setSelectedId] = React.useState<string | null>(null);
  React.useEffect(() => {
    // One candidate → preselect it; several → make the agent pick.
    setSelectedId(projects.length === 1 ? projects[0].id : null);
  }, [projects, open]);

  return (
    <Dialog open={open} onOpenChange={(o) => !o && !linking && onDecline()}>
      <DialogContent className="max-w-lg">
        <DialogHeader>
          <DialogTitle className="flex items-center gap-2">
            <FolderKanban className="h-4 w-4 text-sky-300/90" />
            Link to project?
          </DialogTitle>
          <DialogDescription>
            {projects.length === 1
              ? "This company has an open project ticket. Should this ticket be linked to it?"
              : "This company has open project tickets. Should this ticket be linked to one of them?"}
          </DialogDescription>
        </DialogHeader>

        {/* -m/p pair keeps the selected row's border + ring clear of the
            scroll container's clipping edge. */}
        <ul className="-m-1 max-h-64 space-y-1.5 overflow-y-auto p-1">
          {projects.map((p) => {
            const isSelected = selectedId === p.id;
            return (
              <li key={p.id}>
                <button
                  type="button"
                  onClick={() => setSelectedId(p.id)}
                  className={cn(
                    "w-full rounded-md border px-3 py-2 text-left transition-colors",
                    isSelected
                      ? "border-primary/50 bg-primary/10"
                      : "border-glass bg-glass hover:bg-glass-hover",
                  )}
                >
                  <div className="flex items-center gap-2 text-sm">
                    <span className="font-medium text-foreground/90">#{p.number}</span>
                    <span className="truncate text-foreground/80">{p.subject}</span>
                  </div>
                </button>
              </li>
            );
          })}
        </ul>

        <DialogFooter>
          <Button variant="ghost" onClick={onDecline} disabled={linking}>
            No, keep it separate
          </Button>
          <Button
            onClick={() => selectedId && onLink(selectedId)}
            disabled={!selectedId || linking}
          >
            {linking ? "Linking..." : "Link to project"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
