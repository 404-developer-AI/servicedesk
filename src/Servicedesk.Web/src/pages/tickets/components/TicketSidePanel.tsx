import * as React from "react";
import { toast } from "sonner";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { errorMessage } from "@/lib/http";
import { MergeTicketDialog } from "@/components/MergeTicketDialog";
import { LinkParentDialog } from "@/components/LinkParentDialog";
import { LinkProjectDialog } from "@/components/LinkProjectDialog";
import { ticketApi as ticketApiClient } from "@/lib/ticket-api";
import { cn } from "@/lib/utils";
import type { TicketFormSummary } from "@/lib/forms-api";
import type { Ticket, TicketFieldUpdate } from "@/lib/ticket-api";
import { contactApi, companyApi } from "@/lib/companies-api";
import { Tooltip, TooltipContent, TooltipTrigger, TooltipProvider } from "@/components/ui/tooltip";
import { Pin, PinOff } from "lucide-react";
import { StatusTab } from "./sidePanel/StatusTab";
import { ContactTab } from "./sidePanel/ContactTab";
import { CompanyTab } from "./sidePanel/CompanyTab";

type TicketSidePanelProps = {
  ticket: Ticket;
  /// v0.1.61 — back-office soft lock: status, pending till, queue, priority
  /// and owner are greyed (UI only; "Edit anyway" lifts it).
  softLocked?: boolean;
  onUpdate: (fields: TicketFieldUpdate) => Promise<void>;
  onRequestCompanyAssign?: () => void;
  pinned?: boolean;
  onTogglePin?: () => void;
  /// Timeline view control — lets the agent reveal the otherwise-hidden
  /// system/audit events in the activity feed for the duration of this
  /// ticket view. State lives in the detail page (above both the feed and
  /// this panel) so the choice survives the panel being collapsed.
  showSystemEvents?: boolean;
  onToggleSystemEvents?: () => void;
  systemEventCount?: number;
  /// Relationship strip — all of these come from the detail-response and
  /// drive the "Relationships" block in the Status tab.
  mergedIntoTicketNumber?: string | null;
  mergedSourceTicketNumbers?: number[];
  mergedByUserName?: string | null;
  parentTicketNumber?: string | null;
  parentLinkedByUserName?: string | null;
  childTickets?: { id: string; number: string }[];
  onUnlinkParent?: () => Promise<void> | void;
  /// v0.0.105 — project tickets. `projectsEnabled` gates every project
  /// surface; the rest comes from the detail response like the parent
  /// strip above.
  projectsEnabled?: boolean;
  projectTicketNumber?: string | null;
  projectLinkedByUserName?: string | null;
  projectLinkedTicketCount?: number;
  /// v0.1.33 — server's first-render answer for the "Contact not linked"
  /// banner; the live lookups below take over once they have loaded.
  contactNotLinkedWarning?: boolean;
  /// Orders tagged in the ticket via "::" pills (description or any
  /// article/note), deduped by the detail page. Drives the "Orders" block
  /// in the Status tab; the label is the pill's own "OR3 · Customer" text
  /// used as a fallback while the mirror row loads.
  taggedOrders?: { id: string; label: string }[];
  /// v0.1.52 — filled forms on this ticket; rows open the form dialog via
  /// the page's TicketFormHost (`data-form-id` click).
  ticketForms?: TicketFormSummary[];
};

type TabId = "status" | "contact" | "company";

export function TicketSidePanel({
  ticket,
  onUpdate,
  onRequestCompanyAssign,
  pinned = false,
  onTogglePin,
  showSystemEvents = false,
  onToggleSystemEvents,
  systemEventCount = 0,
  mergedIntoTicketNumber,
  mergedSourceTicketNumbers,
  mergedByUserName,
  parentTicketNumber,
  parentLinkedByUserName,
  childTickets,
  onUnlinkParent,
  projectsEnabled = false,
  projectTicketNumber,
  projectLinkedByUserName,
  projectLinkedTicketCount = 0,
  contactNotLinkedWarning,
  taggedOrders,
  ticketForms,
  softLocked = false,
}: TicketSidePanelProps) {
  const queryClient = useQueryClient();
  const [activeTab, setActiveTab] = React.useState<TabId>("status");
  const [mergeOpen, setMergeOpen] = React.useState(false);
  const [linkParentOpen, setLinkParentOpen] = React.useState(false);
  const [linkProjectOpen, setLinkProjectOpen] = React.useState(false);

  // v0.0.105 — project mutations. Every rule (merged, already project,
  // still-linked tickets, …) is enforced server-side; the 409 message is
  // surfaced as-is so the agent knows why an action was refused.
  const runProjectAction = React.useCallback(
    async (action: () => Promise<unknown>, successMessage: string) => {
      try {
        await action();
        toast.success(successMessage);
        queryClient.invalidateQueries({ queryKey: ["ticket", ticket.id] });
      } catch (err) {
        toast.error(errorMessage(err, "Action failed"));
      }
    },
    [queryClient, ticket.id],
  );

  const { data: contact } = useQuery({
    queryKey: ["contact", ticket.requesterContactId],
    queryFn: () => contactApi.get(ticket.requesterContactId),
    staleTime: 300_000,
  });

  // The ticket's frozen company (set at intake, see DatabaseBootstrapper
  // v0.0.9 step 3) is the source of truth for the Company tab — not the
  // requester's current primary, which may drift after a jobwissel.
  const { data: companyDetail } = useQuery({
    queryKey: ["company", ticket.companyId],
    queryFn: () => companyApi.get(ticket.companyId!),
    staleTime: 300_000,
    enabled: !!ticket.companyId,
  });

  const contactLabel = contact
    ? contact.firstName || contact.lastName
      ? `${contact.firstName} ${contact.lastName}`.trim()
      : contact.email
    : "Contact";

  const companyLabel = companyDetail?.company.name ?? "Company";

  const tabs: { id: TabId; label: string }[] = [
    { id: "status", label: "Status" },
    { id: "contact", label: contactLabel },
    { id: "company", label: companyLabel },
  ];

  return (
    <div className="glass-card w-[320px] shrink-0 flex flex-col min-h-0 h-full">
      {/* Tab bar — pin button on the trailing edge controls the
          per-user "always open" preference for this panel across all tickets. */}
      <div className="flex items-stretch border-b border-glass shrink-0">
        <div className="flex flex-1 min-w-0">
          {tabs.map((tab) => (
            <button
              key={tab.id}
              type="button"
              onClick={() => setActiveTab(tab.id)}
              className={cn(
                "flex-1 min-w-0 px-2 py-2.5 text-xs font-medium truncate transition-colors relative",
                activeTab === tab.id
                  ? "text-foreground"
                  : "text-muted-foreground hover:text-foreground/80",
              )}
            >
              <span className="truncate">{tab.label}</span>
              {activeTab === tab.id && (
                <span className="absolute bottom-0 inset-x-2 h-0.5 rounded-full bg-primary" />
              )}
            </button>
          ))}
        </div>
        {onTogglePin && (
          <TooltipProvider delayDuration={200}>
            <Tooltip>
              <TooltipTrigger asChild>
                <button
                  type="button"
                  onClick={onTogglePin}
                  aria-pressed={pinned}
                  aria-label={pinned ? "Unpin side panel" : "Pin side panel open"}
                  className={cn(
                    "shrink-0 px-2.5 flex items-center justify-center transition-colors border-l border-glass",
                    pinned
                      ? "text-primary hover:text-primary/80 bg-primary/8"
                      : "text-muted-foreground/50 hover:text-foreground hover:bg-glass-hover",
                  )}
                >
                  {pinned
                    ? <Pin className="h-3.5 w-3.5 fill-current" />
                    : <PinOff className="h-3.5 w-3.5" />}
                </button>
              </TooltipTrigger>
              <TooltipContent side="bottom">
                {pinned
                  ? "Pinned — side panel opens automatically on every ticket"
                  : "Pin side panel — keep it open on every ticket"}
              </TooltipContent>
            </Tooltip>
          </TooltipProvider>
        )}
      </div>

      {/* Tab content */}
      <div className="p-4 space-y-4 flex-1 overflow-y-auto">
        {activeTab === "status" && (
          <StatusTab
            ticket={ticket}
            onUpdate={onUpdate}
            companyDetail={companyDetail ?? null}
            onRequestCompanyAssign={onRequestCompanyAssign}
            showSystemEvents={showSystemEvents}
            onToggleSystemEvents={onToggleSystemEvents}
            systemEventCount={systemEventCount}
            onRequestMerge={() => setMergeOpen(true)}
            onRequestLinkParent={() => setLinkParentOpen(true)}
            mergedIntoTicketNumber={mergedIntoTicketNumber ?? null}
            mergedSourceTicketNumbers={mergedSourceTicketNumbers ?? []}
            mergedByUserName={mergedByUserName ?? null}
            parentTicketNumber={parentTicketNumber ?? null}
            parentLinkedByUserName={parentLinkedByUserName ?? null}
            childTickets={childTickets ?? []}
            onUnlinkParent={onUnlinkParent}
            projectsEnabled={projectsEnabled}
            projectTicketNumber={projectTicketNumber ?? null}
            projectLinkedByUserName={projectLinkedByUserName ?? null}
            projectLinkedTicketCount={projectLinkedTicketCount}
            contactNotLinkedWarning={contactNotLinkedWarning}
            taggedOrders={taggedOrders ?? []}
            ticketForms={ticketForms ?? []}
            softLocked={softLocked}
            onRequestLinkProject={() => setLinkProjectOpen(true)}
            onUnlinkProject={() =>
              runProjectAction(() => ticketApiClient.unlinkProject(ticket.id), "Removed from project")
            }
            onRevertProject={() =>
              runProjectAction(() => ticketApiClient.revertProject(ticket.id), "Converted back to a normal ticket")
            }
          />
        )}
        {activeTab === "contact" && (
          <ContactTab
            contact={contact ?? null}
            ticketId={ticket.id}
            currentContactId={ticket.requesterContactId}
            currentCompanyId={ticket.companyId}
          />
        )}
        {activeTab === "company" && (
          <CompanyTab
            ticket={ticket}
            companyDetail={companyDetail ?? null}
            onRequestCompanyAssign={onRequestCompanyAssign}
          />
        )}
      </div>
      <MergeTicketDialog
        open={mergeOpen}
        source={ticket}
        onClose={() => setMergeOpen(false)}
      />
      <LinkParentDialog
        open={linkParentOpen}
        source={ticket}
        onClose={() => setLinkParentOpen(false)}
      />
      <LinkProjectDialog
        open={linkProjectOpen}
        source={ticket}
        onClose={() => setLinkProjectOpen(false)}
      />
    </div>
  );
}

