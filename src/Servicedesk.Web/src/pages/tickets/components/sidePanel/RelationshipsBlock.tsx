import * as React from "react";
import { toast } from "sonner";
import { useQuery } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import { LinkedTicketTypeDialog } from "@/components/LinkedTicketTypeDialog";
import { NewTicketDrawer } from "@/shell/NewTicketDrawer";
import { ticketApi as ticketApiClient, type LinkedTicketPrefill } from "@/lib/ticket-api";
import { Button } from "@/components/ui/button";
import type { Ticket } from "@/lib/ticket-api";
import { FolderKanban, GitMerge, Link2, PlusCircle, Unlink } from "lucide-react";

/* ─── Relationships block ─── */

/// Consolidates every "this ticket is connected to another" snippet in
/// one place: merged-into, merged-from, manual main/sub-ticket links,
/// "Create linked ticket" entry-point. Renders nothing when no relations
/// exist AND the ticket has no parent yet — keeps the side panel quiet
/// for the common case while making the buttons one click away via the
/// always-visible "Link to main ticket" / "Create linked ticket" entries
/// when the merged ticket isn't already terminal.
export function RelationshipsBlock({
  ticket,
  mergedIntoTicketNumber,
  mergedSourceTicketNumbers,
  mergedByUserName,
  parentTicketNumber,
  parentLinkedByUserName,
  childTickets,
  showRelationships,
  onRequestLinkParent,
  onUnlinkParent,
  projectsEnabled,
  projectTicketNumber,
  projectLinkedByUserName,
  projectLinkedTicketCount,
  onRequestLinkProject,
  onUnlinkProject,
  onRevertProject,
}: {
  ticket: Ticket;
  mergedIntoTicketNumber: string | null;
  mergedSourceTicketNumbers: number[];
  mergedByUserName: string | null;
  parentTicketNumber: string | null;
  parentLinkedByUserName: string | null;
  childTickets: { id: string; number: string }[];
  showRelationships: boolean;
  onRequestLinkParent: () => void;
  onUnlinkParent?: () => Promise<void> | void;
  projectsEnabled: boolean;
  projectTicketNumber: string | null;
  projectLinkedByUserName: string | null;
  projectLinkedTicketCount: number;
  onRequestLinkProject: () => void;
  onUnlinkProject: () => void;
  onRevertProject: () => void;
}) {
  // Merged tickets are frozen — hide the manual-link entry points so
  // the agent doesn't try to set a parent on something that already
  // redirects all activity to the merge target.
  const isMerged = !!ticket.mergedIntoTicketId;
  return (
    <div className="space-y-2 rounded-md border border-glass-strong bg-glass px-2.5 py-2">
      <div className="text-[10px] uppercase tracking-wider text-muted-foreground/70">
        Relationships
      </div>

      {mergedIntoTicketNumber && ticket.mergedIntoTicketId && (
        <div className="flex items-start gap-2 text-xs text-foreground/80">
          <GitMerge className="h-3.5 w-3.5 mt-0.5 text-muted-foreground/60 shrink-0" />
          <div className="min-w-0">
            <span className="text-muted-foreground/70">Merged into </span>
            <Link
              to="/tickets/$ticketId"
              params={{ ticketId: ticket.mergedIntoTicketId }}
              className="text-primary hover:underline font-medium"
            >
              #{mergedIntoTicketNumber}
            </Link>
            {mergedByUserName && (
              <span className="text-muted-foreground/60"> · by {mergedByUserName}</span>
            )}
          </div>
        </div>
      )}

      {mergedSourceTicketNumbers.length > 0 && (
        <div className="flex items-start gap-2 text-xs text-foreground/80">
          <GitMerge className="h-3.5 w-3.5 mt-0.5 text-muted-foreground/60 shrink-0 rotate-180" />
          <div className="min-w-0">
            <span className="text-muted-foreground/70">Merged from </span>
            <span className="text-foreground/80">
              {mergedSourceTicketNumbers.map((n, i) => (
                <React.Fragment key={n}>
                  {i > 0 && ", "}
                  <span className="font-medium">#{n}</span>
                </React.Fragment>
              ))}
            </span>
          </div>
        </div>
      )}

      {parentTicketNumber && ticket.parentTicketId && (
        <div className="flex items-start gap-2 text-xs text-foreground/80">
          <Link2 className="h-3.5 w-3.5 mt-0.5 text-muted-foreground/60 shrink-0" />
          <div className="min-w-0 flex-1">
            <span className="text-muted-foreground/70">Main ticket </span>
            <Link
              to="/tickets/$ticketId"
              params={{ ticketId: ticket.parentTicketId }}
              className="text-primary hover:underline font-medium"
            >
              #{parentTicketNumber}
            </Link>
            {parentLinkedByUserName && (
              <span className="text-muted-foreground/60"> · linked by {parentLinkedByUserName}</span>
            )}
          </div>
          {!isMerged && onUnlinkParent && (
            <button
              type="button"
              onClick={() => onUnlinkParent()}
              title="Unlink from main ticket"
              className="text-muted-foreground/50 hover:text-destructive p-0.5"
            >
              <Unlink className="h-3 w-3" />
            </button>
          )}
        </div>
      )}

      {childTickets.length > 0 && (
        <div className="flex items-start gap-2 text-xs text-foreground/80">
          <Link2 className="h-3.5 w-3.5 mt-0.5 text-muted-foreground/60 shrink-0" />
          <div className="min-w-0">
            <span className="text-muted-foreground/70">Sub tickets </span>
            <span className="text-foreground/80">
              {childTickets.map((c, i) => (
                <React.Fragment key={c.id}>
                  {i > 0 && ", "}
                  <Link
                    to="/tickets/$ticketId"
                    params={{ ticketId: c.id }}
                    className="text-primary hover:underline font-medium"
                  >
                    #{c.number}
                  </Link>
                </React.Fragment>
              ))}
            </span>
          </div>
        </div>
      )}

      {/* v0.0.105 — project link on a normal ticket */}
      {projectsEnabled && ticket.projectTicketId && projectTicketNumber && (
        <div className="flex items-start gap-2 text-xs text-foreground/80">
          <FolderKanban className="h-3.5 w-3.5 mt-0.5 text-sky-300/80 shrink-0" />
          <div className="min-w-0 flex-1">
            <span className="text-muted-foreground/70">Project </span>
            <Link
              to="/tickets/$ticketId"
              params={{ ticketId: ticket.projectTicketId }}
              className="text-primary hover:underline font-medium"
            >
              #{projectTicketNumber}
            </Link>
            {projectLinkedByUserName && (
              <span className="text-muted-foreground/60"> · linked by {projectLinkedByUserName}</span>
            )}
          </div>
          {!isMerged && (
            <button
              type="button"
              onClick={onUnlinkProject}
              title="Remove from project"
              className="text-muted-foreground/50 hover:text-destructive p-0.5"
            >
              <Unlink className="h-3 w-3" />
            </button>
          )}
        </div>
      )}

      {/* v0.0.105 — project marker on the project ticket itself */}
      {projectsEnabled && ticket.isProject && (
        <div className="flex items-start gap-2 text-xs text-foreground/80">
          <FolderKanban className="h-3.5 w-3.5 mt-0.5 text-sky-300/80 shrink-0" />
          <div className="min-w-0 flex-1">
            <span className="text-foreground/85 font-medium">Project ticket</span>
            <span className="text-muted-foreground/60">
              {" "}· {projectLinkedTicketCount} linked ticket{projectLinkedTicketCount === 1 ? "" : "s"}
            </span>
          </div>
          {!isMerged && projectLinkedTicketCount === 0 && (
            <button
              type="button"
              onClick={onRevertProject}
              title="Convert back to a normal ticket"
              className="text-muted-foreground/50 hover:text-destructive p-0.5"
            >
              <Unlink className="h-3 w-3" />
            </button>
          )}
        </div>
      )}

      {!showRelationships && (
        <div className="text-[11px] text-muted-foreground/50 italic">
          No related tickets yet.
        </div>
      )}

      {!isMerged && (
        <div className="flex flex-col gap-1.5 pt-1">
          {!ticket.parentTicketId && (
            <Button
              type="button"
              variant="outline"
              size="sm"
              className="w-full justify-start gap-2 text-xs"
              onClick={onRequestLinkParent}
            >
              <Link2 className="h-3.5 w-3.5" />
              Link to main ticket
            </Button>
          )}
          {projectsEnabled && !ticket.isProject && !ticket.projectTicketId && (
            <Button
              type="button"
              variant="outline"
              size="sm"
              className="w-full justify-start gap-2 text-xs"
              onClick={onRequestLinkProject}
            >
              <FolderKanban className="h-3.5 w-3.5" />
              Link to project
            </Button>
          )}
          <LinkedTicketLauncher ticket={ticket} />
        </div>
      )}
    </div>
  );
}

/// v0.0.39 — two-step entry point for creating a linked ticket. The
/// agent clicks the side-panel button → a dialog lists every active
/// manual trigger (admin-configured under Settings → Triggers), each
/// rendered with the ticket-type's icon/colour/label. Picking one
/// fetches a server-rendered prefill (subject/body templates resolved,
/// requester/company looked up per the trigger's source rules) and
/// opens the existing new-ticket drawer pre-filled. Falls back
/// gracefully when no presets exist: an empty-state nudges the admin
/// to configure one.
function LinkedTicketLauncher({ ticket }: { ticket: Ticket }) {
  const [typeDialogOpen, setTypeDialogOpen] = React.useState(false);
  const [drawerOpen, setDrawerOpen] = React.useState(false);
  // Mounted from the first open on (keeps the close animation) — never
  // before, so a closed drawer costs no queries.
  const [drawerUsed, setDrawerUsed] = React.useState(false);
  if (drawerOpen && !drawerUsed) setDrawerUsed(true);
  const [prefill, setPrefill] = React.useState<LinkedTicketPrefill | null>(null);
  const [loadingPrefill, setLoadingPrefill] = React.useState(false);

  const presetsQ = useQuery({
    queryKey: ["linked-ticket-presets", ticket.id],
    queryFn: () => ticketApiClient.listLinkedTicketPresets(ticket.id),
    enabled: typeDialogOpen,
    staleTime: 30_000,
  });

  const handleSelect = async (triggerId: string) => {
    setLoadingPrefill(true);
    try {
      const fetched = await ticketApiClient.getLinkedTicketPrefill(ticket.id, triggerId);
      setPrefill(fetched);
      setTypeDialogOpen(false);
      setDrawerOpen(true);
    } catch {
      toast.error("Failed to load preset prefill");
    } finally {
      setLoadingPrefill(false);
    }
  };

  const handleDrawerOpenChange = (next: boolean) => {
    setDrawerOpen(next);
    // Drop the cached prefill once the drawer closes so a fresh
    // open re-fetches against the parent's current state (queue,
    // requester, company can change between opens).
    if (!next) setPrefill(null);
  };

  return (
    <>
      <Button
        type="button"
        variant="outline"
        size="sm"
        className="w-full justify-start gap-2 text-xs"
        onClick={() => setTypeDialogOpen(true)}
      >
        <PlusCircle className="h-3.5 w-3.5" />
        Create linked ticket
      </Button>
      <LinkedTicketTypeDialog
        open={typeDialogOpen}
        onOpenChange={setTypeDialogOpen}
        presets={presetsQ.data}
        loading={presetsQ.isLoading || loadingPrefill}
        onSelect={(preset) => handleSelect(preset.triggerId)}
      />
      {/* v0.1.25 — mounted only when used: a closed drawer still ran its
          taxonomy/settings queries on every ticket open. */}
      {(drawerUsed || prefill) && <NewTicketDrawer
        initialContactId={prefill?.requesterContactId ?? ticket.requesterContactId}
        initialQueueId={prefill?.queueId ?? ticket.queueId}
        parentTicketId={ticket.id}
        initialSubject={prefill?.subject}
        initialBodyHtml={prefill?.bodyHtml}
        initialStatusId={prefill?.statusId}
        initialPriorityId={prefill?.priorityId}
        initialCategoryId={prefill?.categoryId ?? undefined}
        initialAssigneeUserId={prefill?.assigneeUserId ?? undefined}
        ticketTypeId={prefill?.ticketTypeId}
        initialNote={prefill?.initialNote ?? undefined}
        open={drawerOpen}
        onOpenChange={handleDrawerOpenChange}
      />}
    </>
  );
}
