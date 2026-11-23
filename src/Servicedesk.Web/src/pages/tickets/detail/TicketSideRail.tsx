import * as React from "react";
import type { QueryClient } from "@tanstack/react-query";
import { FolderKanban, ListChecks, PanelRightClose, PanelRightOpen } from "lucide-react";
import { cn } from "@/lib/utils";
import { ticketApi, type Ticket } from "@/lib/ticket-api";
import type { ChecklistSettings, TicketChecklist } from "@/lib/checklist-api";
import type { TicketFormSummary } from "@/lib/forms-api";
import { summarizeChecklists } from "../components/checklists/useTicketChecklists";
import { TicketChecklistPanel } from "../components/checklists/TicketChecklistPanel";
import { TicketProjectPanel } from "../components/projects/TicketProjectPanel";
import { TicketSidePanel } from "../components/TicketSidePanel";
import type { TicketFieldUpdate } from "@/lib/ticket-api";

export function TicketSideRail({
  ticketId,
  ticket,
  queryClient,
  checklistsEnabled,
  checklistSettings,
  checklists,
  checklistOpen,
  onChecklistOpenChange,
  activeChecklistId,
  onActiveChecklistChange,
  projectsEnabled,
  projectOpen,
  onProjectOpenChange,
  sidePanelExpanded,
  setSidePanelExpanded,
  sidePanelPinned,
  onTogglePin,
  showSystemEvents,
  onToggleSystemEvents,
  systemEventCount,
  onSidePanelUpdate,
  onRequestCompanyAssign,
  mergedIntoTicketNumber,
  mergedSourceTicketNumbers,
  mergedByUserName,
  parentTicketNumber,
  parentLinkedByUserName,
  childTickets,
  projectTicketNumber,
  projectLinkedByUserName,
  projectLinkedTicketCount,
  contactNotLinkedWarning,
  taggedOrders,
  ticketForms,
  softLocked,
}: {
  ticketId: string;
  ticket: Ticket;
  queryClient: QueryClient;
  checklistsEnabled: boolean;
  checklistSettings: ChecklistSettings | null;
  checklists: TicketChecklist[];
  checklistOpen: boolean;
  onChecklistOpenChange: (open: boolean) => void;
  activeChecklistId: string | null;
  onActiveChecklistChange: (id: string) => void;
  projectsEnabled: boolean;
  projectOpen: boolean;
  onProjectOpenChange: (open: boolean) => void;
  sidePanelExpanded: boolean;
  setSidePanelExpanded: React.Dispatch<React.SetStateAction<boolean>>;
  sidePanelPinned: boolean;
  onTogglePin: () => void;
  showSystemEvents: boolean;
  onToggleSystemEvents: () => void;
  systemEventCount: number;
  onSidePanelUpdate: (fields: TicketFieldUpdate) => Promise<void>;
  onRequestCompanyAssign: () => void;
  mergedIntoTicketNumber: string | null;
  mergedSourceTicketNumbers: number[];
  mergedByUserName: string | null;
  parentTicketNumber: string | null;
  parentLinkedByUserName: string | null;
  childTickets: { id: string; number: string }[];
  projectTicketNumber: string | null;
  projectLinkedByUserName: string | null;
  projectLinkedTicketCount: number;
  contactNotLinkedWarning?: boolean;
  taggedOrders: { id: string; label: string }[];
  ticketForms: TicketFormSummary[];
  softLocked: boolean;
}) {
  const checklistSummary = React.useMemo(() => summarizeChecklists(checklists), [checklists]);

  // Right column — toggle rail + animated side panel.
  // Both live inside a single shrink-0 wrapper so the parent's gap-6
  // stays between the activity feed and this whole assembly. When the
  // panel is collapsed, only the rail remains visible.
  return (
    <div className="flex shrink-0 items-stretch gap-2">
      <div className="flex flex-col items-center gap-1 pt-1">
        <button
          type="button"
          onClick={() => {
            if (checklistOpen || projectOpen) {
              // Switching back from a docked panel: show details.
              onChecklistOpenChange(false);
              onProjectOpenChange(false);
              setSidePanelExpanded(true);
            } else {
              setSidePanelExpanded((v) => !v);
            }
          }}
          title={checklistOpen || projectOpen ? "Show ticket details" : sidePanelExpanded ? "Collapse side panel" : "Expand side panel"}
          aria-label={checklistOpen || projectOpen ? "Show ticket details" : sidePanelExpanded ? "Collapse side panel" : "Expand side panel"}
          className={cn(
            "p-1.5 rounded-md text-muted-foreground/60 hover:text-foreground hover:bg-glass-hover transition-colors",
            !checklistOpen && !projectOpen && sidePanelExpanded && "text-foreground",
          )}
        >
          {sidePanelExpanded && !checklistOpen && !projectOpen
            ? <PanelRightClose className="h-4 w-4" />
            : <PanelRightOpen className="h-4 w-4" />}
        </button>
        {checklistsEnabled && (
          <button
            type="button"
            onClick={() => {
              onProjectOpenChange(false);
              onChecklistOpenChange(!checklistOpen);
            }}
            title={checklistOpen ? "Hide checklist panel" : "Show checklist panel"}
            aria-label={checklistOpen ? "Hide checklist panel" : "Show checklist panel"}
            className={cn(
              "relative p-1.5 rounded-md transition-colors hover:bg-glass-hover",
              checklistOpen
                ? "text-violet-200 bg-violet-400/15"
                : checklistSummary.count > 0 && !checklistSummary.allComplete
                  ? "text-amber-300/90 hover:text-amber-200"
                  : "text-muted-foreground/60 hover:text-foreground",
            )}
          >
            <ListChecks className="h-4 w-4" />
            {checklistSummary.count > 0 && !checklistSummary.allComplete && (
              <span className="absolute -right-0.5 -top-0.5 h-2 w-2 rounded-full bg-amber-400 ring-2 ring-background" aria-hidden />
            )}
          </button>
        )}
        {/* v0.0.105 — project panel toggle, only on project tickets. */}
        {projectsEnabled && ticket.isProject && (
          <button
            type="button"
            onClick={() => {
              onChecklistOpenChange(false);
              onProjectOpenChange(!projectOpen);
            }}
            title={projectOpen ? "Hide project panel" : "Show project panel"}
            aria-label={projectOpen ? "Hide project panel" : "Show project panel"}
            className={cn(
              "p-1.5 rounded-md transition-colors hover:bg-glass-hover",
              projectOpen
                ? "text-sky-200 bg-sky-400/15"
                : "text-muted-foreground/60 hover:text-foreground",
            )}
          >
            <FolderKanban className="h-4 w-4" />
          </button>
        )}
      </div>
      {checklistsEnabled && checklistOpen && checklistSettings ? (
        <div className="w-[440px] min-h-0 overflow-hidden">
          <TicketChecklistPanel
            ticketId={ticketId}
            checklists={checklists}
            settings={checklistSettings}
            activeChecklistId={activeChecklistId}
            onActiveChange={onActiveChecklistChange}
            onClose={() => {
              onChecklistOpenChange(false);
              setSidePanelExpanded(true);
            }}
            mode="docked"
          />
        </div>
      ) : projectsEnabled && ticket.isProject && projectOpen ? (
        <div className="w-[440px] min-h-0 overflow-hidden">
          <TicketProjectPanel
            ticketId={ticketId}
            ticketNumber={ticket.number}
            onClose={() => {
              onProjectOpenChange(false);
              setSidePanelExpanded(true);
            }}
          />
        </div>
      ) : (
      <div
        className={cn(
          "overflow-hidden transition-[width,opacity] duration-200 ease-out",
          sidePanelExpanded ? "w-[320px] opacity-100" : "w-0 opacity-0",
        )}
        aria-hidden={!sidePanelExpanded}
      >
        <TicketSidePanel
          ticket={ticket}
          onUpdate={onSidePanelUpdate}
          onRequestCompanyAssign={onRequestCompanyAssign}
          pinned={sidePanelPinned}
          onTogglePin={onTogglePin}
          showSystemEvents={showSystemEvents}
          onToggleSystemEvents={onToggleSystemEvents}
          systemEventCount={systemEventCount}
          mergedIntoTicketNumber={mergedIntoTicketNumber}
          mergedSourceTicketNumbers={mergedSourceTicketNumbers}
          mergedByUserName={mergedByUserName}
          parentTicketNumber={parentTicketNumber}
          parentLinkedByUserName={parentLinkedByUserName}
          childTickets={childTickets}
          onUnlinkParent={async () => {
            await ticketApi.unlinkParent(ticketId);
            queryClient.invalidateQueries({ queryKey: ["ticket", ticketId] });
          }}
          projectsEnabled={projectsEnabled}
          projectTicketNumber={projectTicketNumber}
          projectLinkedByUserName={projectLinkedByUserName}
          projectLinkedTicketCount={projectLinkedTicketCount}
          contactNotLinkedWarning={contactNotLinkedWarning}
          taggedOrders={taggedOrders}
          ticketForms={ticketForms}
          softLocked={softLocked}
        />
      </div>
      )}
    </div>
  );
}
