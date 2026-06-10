import * as React from "react";
import { toast } from "sonner";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import { agentQueueApi, settingsApi, taxonomyApi } from "@/lib/api";
import { useServerTime, toServerLocal } from "@/hooks/useServerTime";
import { AgentPicker } from "@/components/AgentPicker";
import { ContactFormDialog } from "@/components/ContactFormDialog";
import { AddContactLinkDialog } from "@/components/AddContactLinkDialog";
import { SwitchRequesterDialog } from "@/components/SwitchRequesterDialog";
import { MergeTicketDialog } from "@/components/MergeTicketDialog";
import { LinkParentDialog } from "@/components/LinkParentDialog";
import { LinkedTicketTypeDialog } from "@/components/LinkedTicketTypeDialog";
import { SyncOrdersButton } from "@/pages/orders/SyncOrdersButton";
import { NewTicketDrawer } from "@/shell/NewTicketDrawer";
import { ticketApi as ticketApiClient, type LinkedTicketPrefill } from "@/lib/ticket-api";
import { AddCompanyContactDialog } from "@/components/AddCompanyContactDialog";
import { CompanyEditDialog } from "@/components/CompanyEditDialog";
import { TaxonomySelect } from "@/components/TaxonomySelect";
import { PendingTillField } from "@/components/PendingTillField";
import { Button } from "@/components/ui/button";
import { cn } from "@/lib/utils";
import type {
  Ticket,
  TicketFieldUpdate,
  Contact,
  CompanyDetail,
  ContactCompanyLink,
  ContactCompanyOption,
  ContactCompanyRole,
} from "@/lib/ticket-api";
import { contactApi, companyApi } from "@/lib/ticket-api";
import { usePresenceStore, type PresenceUser } from "@/stores/usePresenceStore";
import { useAuth } from "@/auth/authStore";
import {
  Tooltip,
  TooltipContent,
  TooltipTrigger,
  TooltipProvider,
} from "@/components/ui/tooltip";
import {
  Building2,
  Eye,
  EyeOff,
  GitMerge,
  Globe,
  Link2,
  Mail,
  Phone,
  MapPin,
  Briefcase,
  Pencil,
  Pin,
  PinOff,
  Plus,
  PlusCircle,
  Unlink,
  User,
  UserCog,
} from "lucide-react";

type TicketSidePanelProps = {
  ticket: Ticket;
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
};

type TabId = "status" | "contact" | "company";

function FieldLabel({ children }: { children: React.ReactNode }) {
  return (
    <div className="text-xs uppercase tracking-wider text-muted-foreground mb-1">
      {children}
    </div>
  );
}

function FieldRow({
  icon: Icon,
  label,
  children,
}: {
  icon?: React.ComponentType<{ className?: string }>;
  label: string;
  children: React.ReactNode;
}) {
  return (
    <div>
      <div className="flex items-center gap-1.5 text-xs uppercase tracking-wider text-muted-foreground mb-1">
        {Icon && <Icon className="h-3 w-3" />}
        {label}
      </div>
      <div className="text-sm text-foreground/80">{children}</div>
    </div>
  );
}

function ServerDate({ iso }: { iso: string | null | undefined }) {
  const { time } = useServerTime();
  const offset = time?.offsetMinutes ?? 0;
  if (!iso) return <span>Not set</span>;
  return <span>{toServerLocal(iso, offset)}</span>;
}

function EmptyState({ text }: { text: string }) {
  return (
    <div className="text-sm text-muted-foreground/60 italic py-4 text-center">
      {text}
    </div>
  );
}


/// Filter a taxonomy list to active rows, but keep the ticket's current
/// selection in the list (pinned to the front) when it has been
/// deactivated — otherwise the <select> renders blank and the user
/// can't save the ticket.
function withInactiveCarveOut<T extends { id: string; isActive: boolean }>(
  all: T[] | undefined,
  current: T | undefined,
): T[] {
  if (!all) return [];
  const active = all.filter((x) => x.isActive);
  if (current && !current.isActive) return [current, ...active];
  return active;
}

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
}: TicketSidePanelProps) {
  const [activeTab, setActiveTab] = React.useState<TabId>("status");
  const [mergeOpen, setMergeOpen] = React.useState(false);
  const [linkParentOpen, setLinkParentOpen] = React.useState(false);

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
                      ? "text-primary hover:text-primary/80 bg-primary/[0.08]"
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
    </div>
  );
}

/* ─── Status Tab ─── */

function StatusTab({
  ticket,
  onUpdate,
  companyDetail,
  onRequestCompanyAssign,
  onRequestMerge,
  onRequestLinkParent,
  showSystemEvents,
  onToggleSystemEvents,
  systemEventCount,
  mergedIntoTicketNumber,
  mergedSourceTicketNumbers,
  mergedByUserName,
  parentTicketNumber,
  parentLinkedByUserName,
  childTickets,
  onUnlinkParent,
}: {
  ticket: Ticket;
  onUpdate: (fields: TicketFieldUpdate) => Promise<void>;
  companyDetail: CompanyDetail | null;
  onRequestCompanyAssign?: () => void;
  onRequestMerge: () => void;
  onRequestLinkParent: () => void;
  showSystemEvents: boolean;
  onToggleSystemEvents?: () => void;
  systemEventCount: number;
  mergedIntoTicketNumber: string | null;
  mergedSourceTicketNumbers: number[];
  mergedByUserName: string | null;
  parentTicketNumber: string | null;
  parentLinkedByUserName: string | null;
  childTickets: { id: string; number: string }[];
  onUnlinkParent?: () => Promise<void> | void;
}) {
  // v0.0.59 — "Sync orders" button is shown only to users with the Orders
  // feature flag.
  const { user } = useAuth();
  // Pulsing "Contact not linked" warning. Only renders when (a) the admin
  // has the toggle on and (b) the requester has zero current company links.
  const { data: warningSetting } = useQuery({
    queryKey: ["settings", "tickets-warnings"],
    queryFn: () => settingsApi.list("Tickets"),
    staleTime: 60_000,
  });
  const showContactNotLinkedSetting = React.useMemo(
    () =>
      warningSetting?.find((e) => e.key === "Tickets.ShowContactNotLinkedWarning")?.value ===
      "true",
    [warningSetting],
  );
  const { data: requesterLinks } = useQuery({
    queryKey: ["contact-companies", ticket.requesterContactId],
    queryFn: () => contactApi.listCompanies(ticket.requesterContactId),
    staleTime: 60_000,
    enabled: showContactNotLinkedSetting && !!ticket.requesterContactId,
  });
  const showContactNotLinked =
    showContactNotLinkedSetting && (requesterLinks?.length ?? null) === 0;

  const { data: queues } = useQuery({
    queryKey: ["accessible-queues"],
    queryFn: agentQueueApi.list,
    staleTime: 60_000,
  });

  const { data: priorities } = useQuery({
    queryKey: ["priorities"],
    queryFn: taxonomyApi.priorities.list,
    staleTime: 300_000,
  });

  const { data: statuses } = useQuery({
    queryKey: ["statuses"],
    queryFn: taxonomyApi.statuses.list,
    staleTime: 300_000,
  });

  const currentStatus = statuses?.find((s) => s.id === ticket.statusId);
  const currentPriority = priorities?.find((p) => p.id === ticket.priorityId);
  const currentQueue = queues?.find((q) => q.id === ticket.queueId);

  // Each dropdown shows only active rows, but pins the ticket's
  // *current* selection to the front if an admin deactivated it after
  // the ticket was last touched. Without the pin the <select> would
  // render blank for legacy tickets and the user could no longer save.
  // Inactive carve-outs are flagged with a "— inactive" suffix at the
  // render site so the user knows why they can't pick that again later.
  // v0.0.40 polish — restrict the status dropdown to the ticket's
  // current queue's allowed-list. Empty list = no scoping (legacy
  // behaviour, all statuses). The ticket's actual current status is
  // always retained via the inactive-carve-out so a queue rescope
  // doesn't break existing tickets — the dropdown still shows the
  // current value so the agent can change it.
  const queueAllowedStatusIds = currentQueue?.allowedStatusIds ?? [];
  const scopedStatuses = React.useMemo(
    () =>
      queueAllowedStatusIds.length === 0
        ? statuses
        : statuses?.filter((s) => queueAllowedStatusIds.includes(s.id)),
    [statuses, queueAllowedStatusIds],
  );
  const statusOptions = React.useMemo(
    () => withInactiveCarveOut(scopedStatuses, currentStatus),
    [scopedStatuses, currentStatus],
  );
  const priorityOptions = React.useMemo(
    () => withInactiveCarveOut(priorities, currentPriority),
    [priorities, currentPriority],
  );
  const queueOptions = React.useMemo(
    () => withInactiveCarveOut(queues, currentQueue),
    [queues, currentQueue],
  );

  const hasMergeRelations =
    !!mergedIntoTicketNumber || mergedSourceTicketNumbers.length > 0;
  const hasParentRelation = !!parentTicketNumber || !!ticket.parentTicketId;
  const hasChildren = childTickets.length > 0;
  const showRelationships = hasMergeRelations || hasParentRelation || hasChildren;

  return (
    <>
      {user?.adsolutOrdersEnabled && <SyncOrdersButton />}
      {showContactNotLinked && (
        <div
          className="flex items-start gap-2 rounded-md border border-amber-500/40 bg-amber-500/10 px-2.5 py-1.5 text-xs text-amber-200 animate-pulse"
          title="The requester is not linked to any company. Open the Contact tab to add a company link."
        >
          <span
            aria-hidden
            className="mt-1 inline-block h-1.5 w-1.5 shrink-0 rounded-full bg-amber-400 shadow-[0_0_8px_rgba(251,191,36,0.6)]"
          />
          <div className="min-w-0">
            <div className="font-medium">Contact not linked</div>
            <div className="text-[11px] text-amber-300/80">Please link to a company</div>
          </div>
        </div>
      )}
      <div>
        <FieldLabel>Status</FieldLabel>
        <TaxonomySelect
          value={ticket.statusId}
          onChange={(v) => onUpdate({ statusId: v })}
          options={statusOptions.map((s) => ({
            id: s.id,
            name: s.name,
            color: s.color,
            badge: s.stateCategory,
            suffix: !s.isActive ? "— inactive" : undefined,
          }))}
        />
      </div>

      {currentStatus?.stateCategory === "Pending" && (
        <div>
          <FieldLabel>Pending till</FieldLabel>
          <PendingTillField
            value={ticket.pendingTillUtc}
            onCommit={(iso) => onUpdate({ pendingTillUtc: iso })}
          />
        </div>
      )}

      <div>
        <FieldLabel>Queue</FieldLabel>
        <TaxonomySelect
          value={ticket.queueId}
          onChange={(v) => onUpdate({ queueId: v })}
          options={queueOptions.map((q) => ({
            id: q.id,
            name: q.name,
            color: q.color,
            suffix: !q.isActive ? "— inactive" : undefined,
          }))}
        />
      </div>

      <div>
        <FieldLabel>Priority</FieldLabel>
        <TaxonomySelect
          value={ticket.priorityId}
          onChange={(v) => onUpdate({ priorityId: v })}
          options={priorityOptions.map((p) => ({
            id: p.id,
            name: p.name,
            color: p.color,
            suffix: !p.isActive ? "— inactive" : undefined,
          }))}
        />
      </div>

      <div>
        <FieldLabel>Assignee</FieldLabel>
        <AgentPicker
          value={ticket.assigneeUserId}
          onChange={(userId) => onUpdate({ assigneeUserId: userId ?? undefined })}
        />
      </div>

      {/* v0.0.51 — company quick-view + change. Click to open the same
          dialog the Company-tab banner uses. The tab itself still
          carries the full company detail (address, contacts, domains). */}
      <div>
        <FieldLabel>Company</FieldLabel>
        <button
          type="button"
          onClick={onRequestCompanyAssign}
          disabled={!onRequestCompanyAssign}
          className={cn(
            "flex w-full items-center gap-2 rounded-md border border-glass bg-glass px-3 py-2 text-left text-sm transition-colors",
            onRequestCompanyAssign
              ? "hover:bg-glass-hover"
              : "cursor-default",
          )}
        >
          <Building2 className="h-3.5 w-3.5 shrink-0 text-muted-foreground" />
          <span
            className={cn(
              "flex-1 truncate",
              !companyDetail && "text-muted-foreground",
            )}
          >
            {companyDetail?.company.name ?? "No company linked"}
          </span>
          {onRequestCompanyAssign && (
            <Pencil className="h-3 w-3 shrink-0 text-muted-foreground/70" />
          )}
        </button>
        <div className="mt-1">
          <ResolutionBadge
            ticket={ticket}
            onRequestCompanyAssign={onRequestCompanyAssign}
          />
        </div>
      </div>

      <RelationshipsBlock
        ticket={ticket}
        mergedIntoTicketNumber={mergedIntoTicketNumber}
        mergedSourceTicketNumbers={mergedSourceTicketNumbers}
        mergedByUserName={mergedByUserName}
        parentTicketNumber={parentTicketNumber}
        parentLinkedByUserName={parentLinkedByUserName}
        childTickets={childTickets}
        showRelationships={showRelationships}
        onRequestLinkParent={onRequestLinkParent}
        onUnlinkParent={onUnlinkParent}
      />

      {!ticket.mergedIntoTicketId && (
        <Button
          type="button"
          variant="outline"
          size="sm"
          className="w-full justify-start gap-2 text-xs"
          onClick={onRequestMerge}
        >
          <GitMerge className="h-3.5 w-3.5" />
          Merge into another ticket
        </Button>
      )}

      {onToggleSystemEvents && systemEventCount > 0 && (
        <div className="space-y-2 rounded-md border border-glass-strong bg-glass px-2.5 py-2">
          <div className="text-[10px] uppercase tracking-wider text-muted-foreground/70">
            Timeline
          </div>
          <button
            type="button"
            onClick={onToggleSystemEvents}
            aria-pressed={showSystemEvents}
            className={cn(
              "flex w-full items-center gap-2 rounded-md border px-2.5 py-1.5 text-xs transition-colors",
              showSystemEvents
                ? "border-primary/40 bg-primary/[0.08] text-foreground"
                : "border-glass bg-glass text-muted-foreground hover:bg-glass-hover hover:text-foreground",
            )}
          >
            {showSystemEvents ? (
              <EyeOff className="h-3.5 w-3.5 shrink-0" />
            ) : (
              <Eye className="h-3.5 w-3.5 shrink-0" />
            )}
            <span className="flex-1 text-left">
              {showSystemEvents ? "Hide system events" : "Show system events"}
            </span>
            <span className="rounded px-1.5 py-0.5 text-[10px] font-medium border border-glass bg-glass-strong text-muted-foreground/80">
              {systemEventCount}
            </span>
          </button>
          <p className="text-[10px] leading-snug text-muted-foreground/60">
            Status, assignment, priority and other audit events. Resets when
            you leave the ticket.
          </p>
        </div>
      )}

      <ZammadImportBadge ticket={ticket} />

      <TicketPresence ticketId={ticket.id} />
    </>
  );
}

/* ─── Zammad provenance badge ─── */

/// Renders a quiet "Imported from Zammad #<number>" strip in the Status
/// tab for tickets created by the v0.0.41 migration link. Pure metadata —
/// no link out (we don't trust the Zammad install to stay reachable) and
/// no actions. The upstream number is rendered as monospace so it lines up
/// with the local ticket-number chip in the header.
function ZammadImportBadge({ ticket }: { ticket: Ticket }) {
  if (ticket.zammadTicketId == null) return null;
  const upstreamLabel = ticket.zammadTicketNumber ?? String(ticket.zammadTicketId);
  return (
    <div className="rounded-md border border-glass-strong bg-glass px-2.5 py-1.5 text-[11px] text-muted-foreground">
      <div className="text-[9px] uppercase tracking-wider text-muted-foreground/60">
        Provenance
      </div>
      <div className="mt-0.5 flex items-baseline gap-1.5">
        <span>Imported from Zammad</span>
        <span className="font-mono text-foreground/80">#{upstreamLabel}</span>
      </div>
    </div>
  );
}

/* ─── Relationships block ─── */

/// Consolidates every "this ticket is connected to another" snippet in
/// one place: merged-into, merged-from, manual main/sub-ticket links,
/// "Create linked ticket" entry-point. Renders nothing when no relations
/// exist AND the ticket has no parent yet — keeps the side panel quiet
/// for the common case while making the buttons one click away via the
/// always-visible "Link to main ticket" / "Create linked ticket" entries
/// when the merged ticket isn't already terminal.
function RelationshipsBlock({
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
      <NewTicketDrawer
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
      />
    </>
  );
}

/* ─── Contact Tab ─── */

function ContactTab({
  contact,
  ticketId,
  currentContactId,
  currentCompanyId,
}: {
  contact: Contact | null;
  ticketId: string;
  currentContactId: string;
  currentCompanyId: string | null;
}) {
  const qc = useQueryClient();
  const [editing, setEditing] = React.useState(false);
  const [linking, setLinking] = React.useState(false);
  const [switching, setSwitching] = React.useState(false);

  const { data: companyLinks } = useQuery({
    queryKey: ["contact-companies", contact?.id],
    queryFn: () => contactApi.listCompanies(contact!.id),
    enabled: !!contact,
    placeholderData: (prev) => prev,
  });

  if (!contact) return <EmptyState text="Loading contact..." />;

  const fullName =
    contact.firstName || contact.lastName
      ? `${contact.firstName} ${contact.lastName}`.trim()
      : null;

  const existingCompanyIds = new Set((companyLinks ?? []).map((l) => l.companyId));
  const currentPrimary = (companyLinks ?? []).find((l) => l.role === "primary") ?? null;

  return (
    <>
      <div className="flex items-center justify-between -mt-1 mb-1">
        <div className="text-[10px] uppercase tracking-wider text-muted-foreground/70">
          Requester
        </div>
        <div className="flex items-center gap-0.5">
          <Button
            size="sm"
            variant="ghost"
            className="h-6 px-1.5 text-[11px] text-muted-foreground hover:text-foreground"
            onClick={() => setSwitching(true)}
            title="Switch to a different contact"
          >
            <UserCog className="h-3 w-3 mr-1" /> Switch
          </Button>
          <Button
            size="sm"
            variant="ghost"
            className="h-6 px-1.5 text-[11px] text-muted-foreground hover:text-foreground"
            onClick={() => setEditing(true)}
            title="Edit contact details"
          >
            <Pencil className="h-3 w-3 mr-1" /> Edit
          </Button>
        </div>
      </div>

      <ContactFormDialog
        open={editing}
        mode="edit"
        initial={contact}
        onClose={() => setEditing(false)}
        onSaved={() => {
          qc.invalidateQueries({ queryKey: ["contact", contact.id] });
        }}
      />

      <SwitchRequesterDialog
        open={switching}
        ticketId={ticketId}
        currentContactId={currentContactId}
        currentCompanyId={currentCompanyId}
        onClose={() => setSwitching(false)}
      />

      {linking && (
        <AddContactLinkDialog
          contactId={contact.id}
          existingCompanyIds={existingCompanyIds}
          currentPrimary={currentPrimary}
          onClose={() => setLinking(false)}
        />
      )}

      {fullName && (
        <FieldRow icon={User} label="Name">
          {fullName}
        </FieldRow>
      )}

      <FieldRow icon={Mail} label="Email">
        <a
          href={`mailto:${contact.email}`}
          className="text-primary hover:underline break-all"
        >
          {contact.email}
        </a>
      </FieldRow>

      {contact.phone && (
        <FieldRow icon={Phone} label="Phone">
          <a
            href={`tel:${contact.phone}`}
            className="text-primary hover:underline"
          >
            {contact.phone}
          </a>
        </FieldRow>
      )}

      {contact.jobTitle && (
        <FieldRow icon={Briefcase} label="Job title">
          {contact.jobTitle}
        </FieldRow>
      )}

      {contact.companyRole && contact.companyRole !== "Member" && (
        <FieldRow icon={Building2} label="Role">
          {contact.companyRole}
        </FieldRow>
      )}

      <div className="border-t border-glass" />

      <div>
        <FieldLabel>Status</FieldLabel>
        <span
          className={cn(
            "inline-flex items-center rounded px-1.5 py-0.5 text-xs font-medium border",
            contact.isActive
              ? "border-green-500/30 bg-green-500/10 text-green-400"
              : "border-glass bg-glass text-muted-foreground",
          )}
        >
          {contact.isActive ? "Active" : "Inactive"}
        </span>
      </div>

      <div>
        <FieldLabel>Created</FieldLabel>
        <div className="text-sm text-foreground/80"><ServerDate iso={contact.createdUtc} /></div>
      </div>

      <div className="border-t border-glass" />

      <ContactCompanyLinks
        contactId={contact.id}
        links={companyLinks ?? []}
        onLink={() => setLinking(true)}
      />
    </>
  );
}

const LINK_ROLE_BADGE: Record<ContactCompanyRole, string> = {
  primary: "border-purple-400/50 bg-purple-500/20 text-purple-100",
  secondary: "border-sky-400/30 bg-sky-500/15 text-sky-200",
  supplier: "border-amber-400/30 bg-amber-500/15 text-amber-200",
};

/// Compact role-grouped link list rendered inside the ticket side-panel's
/// Contact tab. Mirrors the shape used on the full `/contacts/:id` page but
/// tight enough for the 280px panel width. Each row links to the target
/// company so agents can pivot from the ticket to the company's Contacts-tab
/// without losing context.
function ContactCompanyLinks({
  contactId,
  links,
  onLink,
}: {
  contactId: string;
  links: ContactCompanyOption[];
  onLink: () => void;
}) {
  const ordered = [...links].sort((a, b) => {
    const rank: Record<ContactCompanyRole, number> = { primary: 0, secondary: 1, supplier: 2 };
    if (rank[a.role] !== rank[b.role]) return rank[a.role] - rank[b.role];
    return a.companyName.localeCompare(b.companyName);
  });

  return (
    <div>
      <div className="flex items-center justify-between mb-1">
        <FieldLabel>
          Company links{" "}
          <span className="text-muted-foreground/70">({links.length})</span>
        </FieldLabel>
        <Button
          size="sm"
          variant="ghost"
          className="h-6 px-1.5 text-[11px] text-muted-foreground hover:text-foreground"
          onClick={onLink}
          title="Link a company to this contact"
        >
          <Plus className="h-3 w-3 mr-1" /> Link
        </Button>
      </div>

      {ordered.length === 0 ? (
        <p className="text-[11px] text-muted-foreground/70">
          No links yet. Click{" "}
          <span className="text-foreground">Link</span> to attach the first company.
        </p>
      ) : (
        <ul className="space-y-1">
          {ordered.map((l) => (
            <li key={l.linkId}>
              <Link
                to="/companies/$companyId"
                params={{ companyId: l.companyId }}
                className="flex items-center gap-1.5 rounded-md border border-glass bg-glass px-1.5 py-1 text-xs hover:border-glass-strong hover:bg-glass-hover"
              >
                <span
                  className={cn(
                    "inline-flex items-center rounded-sm border px-1 py-0 text-[9px] font-medium uppercase tracking-wide",
                    LINK_ROLE_BADGE[l.role],
                  )}
                >
                  {l.role[0]}
                </span>
                <Building2 className="h-3 w-3 text-muted-foreground shrink-0" />
                <span className="truncate text-foreground">
                  {l.companyShortName || l.companyName}
                </span>
                <span className="font-mono text-[9px] text-muted-foreground ml-auto shrink-0">
                  {l.companyCode}
                </span>
              </Link>
            </li>
          ))}
        </ul>
      )}
      <div className="mt-1.5 text-[10px] text-muted-foreground/70">
        Ticket's company stays frozen at intake — linking here doesn't retroactively
        move historic tickets.
      </div>
      <Link
        to="/contacts/$contactId"
        params={{ contactId }}
        className="mt-1 inline-flex text-[10px] text-muted-foreground hover:text-foreground"
      >
        Open full contact page →
      </Link>
    </div>
  );
}

/* ─── Company Tab ─── */

const RESOLVED_VIA_LABEL: Record<NonNullable<Ticket["companyResolvedVia"]>, string> = {
  thread_reply: "via thread-reply",
  primary: "via primary link",
  secondary: "via secondary link",
  manual: "manueel toegewezen",
  unresolved: "niet eenduidig",
};

function ResolutionBadge({
  ticket,
  onRequestCompanyAssign,
}: {
  ticket: Ticket;
  onRequestCompanyAssign?: () => void;
}) {
  if (ticket.awaitingCompanyAssignment) {
    const banner = (
      <div className="rounded-md border border-amber-500/30 bg-amber-500/10 text-amber-200 text-xs px-2 py-1.5">
        In afwachting van company-toewijzing
        {onRequestCompanyAssign && (
          <span className="block text-[10px] mt-0.5 text-amber-300/70">
            Klik om toe te wijzen
          </span>
        )}
      </div>
    );
    if (!onRequestCompanyAssign) return banner;
    return (
      <button
        type="button"
        onClick={onRequestCompanyAssign}
        className="block w-full text-left hover:brightness-110 transition"
      >
        {banner}
      </button>
    );
  }
  if (!ticket.companyResolvedVia) return null;
  return (
    <div className="text-[11px] text-muted-foreground/70 italic">
      {RESOLVED_VIA_LABEL[ticket.companyResolvedVia]}
    </div>
  );
}

function CompanyTab({
  ticket,
  companyDetail,
  onRequestCompanyAssign,
}: {
  ticket: Ticket;
  companyDetail: CompanyDetail | null;
  onRequestCompanyAssign?: () => void;
}) {
  const [editing, setEditing] = React.useState(false);
  const [linkingContact, setLinkingContact] = React.useState(false);

  const companyId = companyDetail?.company.id ?? null;

  const { data: companyContacts } = useQuery({
    queryKey: ["companies", "contacts", companyId],
    queryFn: () => companyApi.listContacts(companyId!),
    enabled: !!companyId,
    placeholderData: (prev) => prev,
  });
  const { data: companyLinks } = useQuery({
    queryKey: ["companies", "links", companyId],
    queryFn: () => companyApi.links(companyId!),
    enabled: !!companyId,
    placeholderData: (prev) => prev,
  });

  if (!companyDetail) {
    return (
      <>
        <ResolutionBadge ticket={ticket} onRequestCompanyAssign={onRequestCompanyAssign} />
        <EmptyState text="No company linked" />
      </>
    );
  }

  const { company, domains } = companyDetail;

  const addressParts = [
    company.addressLine1,
    company.addressLine2,
    [company.postalCode, company.city].filter(Boolean).join(" "),
    company.country,
  ].filter(Boolean);

  const hasActiveAlert = !!company.alertText && (company.alertOnCreate || company.alertOnOpen);

  return (
    <>
      <div className="flex items-center justify-between -mt-1 mb-1">
        <div className="text-[10px] uppercase tracking-wider text-muted-foreground/70">
          Company
        </div>
        <Button
          size="sm"
          variant="ghost"
          className="h-6 px-1.5 text-[11px] text-muted-foreground hover:text-foreground"
          onClick={() => setEditing(true)}
          title="Edit company details"
        >
          <Pencil className="h-3 w-3 mr-1" /> Edit
        </Button>
      </div>

      <CompanyEditDialog
        open={editing}
        company={company}
        onClose={() => setEditing(false)}
      />

      <ResolutionBadge ticket={ticket} onRequestCompanyAssign={onRequestCompanyAssign} />

      <FieldRow icon={Building2} label="Name">
        <Link
          to="/companies/$companyId"
          params={{ companyId: company.id }}
          className="inline-flex items-center gap-1 text-primary hover:underline"
        >
          {company.name}
          {hasActiveAlert && (
            <span
              className="inline-block h-1.5 w-1.5 rounded-full bg-amber-400"
              title="This company has an active alert"
              aria-label="active alert"
            />
          )}
        </Link>
      </FieldRow>

      {company.code && (
        <FieldRow label="Customer code">
          <span className="font-mono text-xs text-muted-foreground">{company.code}</span>
        </FieldRow>
      )}

      {company.shortName && (
        <FieldRow label="Short name">{company.shortName}</FieldRow>
      )}

      {company.vatNumber && (
        <FieldRow label="VAT number">
          <span className="font-mono text-xs text-muted-foreground">{company.vatNumber}</span>
        </FieldRow>
      )}

      {company.email && (
        <FieldRow icon={Mail} label="Email">
          <a
            href={`mailto:${company.email}`}
            className="text-primary hover:underline break-all"
          >
            {company.email}
          </a>
        </FieldRow>
      )}

      {company.description && (
        <FieldRow label="Description">
          <span className="text-muted-foreground">{company.description}</span>
        </FieldRow>
      )}

      {company.website && (
        <FieldRow icon={Globe} label="Website">
          <a
            href={company.website.startsWith("http") ? company.website : `https://${company.website}`}
            target="_blank"
            rel="noopener noreferrer"
            className="text-primary hover:underline break-all"
          >
            {company.website}
          </a>
        </FieldRow>
      )}

      {company.phone && (
        <FieldRow icon={Phone} label="Phone">
          <a
            href={`tel:${company.phone}`}
            className="text-primary hover:underline"
          >
            {company.phone}
          </a>
        </FieldRow>
      )}

      {addressParts.length > 0 && (
        <FieldRow icon={MapPin} label="Address">
          <div className="space-y-0.5">
            {addressParts.map((line, i) => (
              <div key={i}>{line}</div>
            ))}
          </div>
        </FieldRow>
      )}

      {domains.length > 0 && (
        <>
          <div className="border-t border-glass" />
          <div>
            <FieldLabel>Domains</FieldLabel>
            <div className="flex flex-wrap gap-1.5">
              {domains.map((d) => (
                <span
                  key={d.id}
                  className="inline-flex items-center gap-1 rounded px-1.5 py-0.5 text-xs border border-glass bg-glass text-muted-foreground"
                >
                  <Globe className="h-3 w-3" />
                  {d.domain}
                </span>
              ))}
            </div>
          </div>
        </>
      )}

      <div className="border-t border-glass" />

      <div>
        <FieldLabel>Status</FieldLabel>
        <span
          className={cn(
            "inline-flex items-center rounded px-1.5 py-0.5 text-xs font-medium border",
            company.isActive
              ? "border-green-500/30 bg-green-500/10 text-green-400"
              : "border-glass bg-glass text-muted-foreground",
          )}
        >
          {company.isActive ? "Active" : "Inactive"}
        </span>
      </div>

      <div className="border-t border-glass" />

      <CompanyContactLinks
        companyId={company.id}
        contacts={companyContacts ?? []}
        links={companyLinks ?? []}
        onLink={() => setLinkingContact(true)}
      />

      {linkingContact && (
        <AddCompanyContactDialog
          companyId={company.id}
          existingContactIds={new Set((companyContacts ?? []).map((c) => c.id))}
          onClose={() => setLinkingContact(false)}
        />
      )}
    </>
  );
}

/// Compact role-grouped contact list rendered inside the ticket side-panel's
/// Company tab. Mirrors the shape of ContactCompanyLinks (the inverse on the
/// Contact tab): single-letter role badge, name/email, click-through to the
/// contact's detail page. Joins the flat contact list with the role-tagged
/// links by contactId so each row shows its role relative to this company.
function CompanyContactLinks({
  companyId,
  contacts,
  links,
  onLink,
}: {
  companyId: string;
  contacts: Contact[];
  links: ContactCompanyLink[];
  onLink: () => void;
}) {
  const roleByContact = React.useMemo(() => {
    const m = new Map<string, ContactCompanyRole>();
    for (const l of links) m.set(l.contactId, l.role);
    return m;
  }, [links]);

  const ordered = React.useMemo(() => {
    const rank: Record<ContactCompanyRole, number> = {
      primary: 0,
      secondary: 1,
      supplier: 2,
    };
    return [...contacts].sort((a, b) => {
      const ra = roleByContact.get(a.id);
      const rb = roleByContact.get(b.id);
      const rankA = ra ? rank[ra] : 99;
      const rankB = rb ? rank[rb] : 99;
      if (rankA !== rankB) return rankA - rankB;
      const na =
        [a.firstName, a.lastName].filter(Boolean).join(" ").trim() || a.email;
      const nb =
        [b.firstName, b.lastName].filter(Boolean).join(" ").trim() || b.email;
      return na.localeCompare(nb);
    });
  }, [contacts, roleByContact]);

  return (
    <div>
      <div className="flex items-center justify-between mb-1">
        <FieldLabel>
          Contacts{" "}
          <span className="text-muted-foreground/70">({contacts.length})</span>
        </FieldLabel>
        <Button
          size="sm"
          variant="ghost"
          className="h-6 px-1.5 text-[11px] text-muted-foreground hover:text-foreground"
          onClick={onLink}
          title="Link a contact to this company"
        >
          <Plus className="h-3 w-3 mr-1" /> Link
        </Button>
      </div>

      {ordered.length === 0 ? (
        <p className="text-[11px] text-muted-foreground/70">
          No contacts linked yet. Click{" "}
          <span className="text-foreground">Link</span> to attach one.
        </p>
      ) : (
        <ul className="space-y-1">
          {ordered.map((c) => {
            const role = roleByContact.get(c.id);
            const fullName =
              [c.firstName, c.lastName].filter(Boolean).join(" ").trim() || c.email;
            return (
              <li key={c.id}>
                <Link
                  to="/contacts/$contactId"
                  params={{ contactId: c.id }}
                  className="flex items-center gap-1.5 rounded-md border border-glass bg-glass px-1.5 py-1 text-xs hover:border-glass-strong hover:bg-glass-hover"
                >
                  {role ? (
                    <span
                      className={cn(
                        "inline-flex items-center rounded-sm border px-1 py-0 text-[9px] font-medium uppercase tracking-wide",
                        LINK_ROLE_BADGE[role],
                      )}
                    >
                      {role[0]}
                    </span>
                  ) : (
                    <span className="inline-flex items-center rounded-sm border border-glass bg-glass px-1 py-0 text-[9px] font-medium uppercase tracking-wide text-muted-foreground">
                      ?
                    </span>
                  )}
                  <User className="h-3 w-3 text-muted-foreground shrink-0" />
                  <span className="truncate text-foreground">{fullName}</span>
                </Link>
              </li>
            );
          })}
        </ul>
      )}
      <Link
        to="/companies/$companyId"
        params={{ companyId }}
        className="mt-1 inline-flex text-[10px] text-muted-foreground hover:text-foreground"
      >
        Open full company page →
      </Link>
    </div>
  );
}

/* ─── Presence ─── */

const EMPTY_PRESENCE: PresenceUser[] = [];

function TicketPresence({ ticketId }: { ticketId: string }) {
  const presence = usePresenceStore((s) => s.byTicket[ticketId] ?? EMPTY_PRESENCE);
  const { user: currentUser } = useAuth();
  const others = presence.filter((u) => u.userId !== currentUser?.id);

  if (others.length === 0) return null;

  return (
    <TooltipProvider delayDuration={200}>
      <>
        <div className="border-t border-glass" />
        <div>
          <FieldLabel>Also viewing</FieldLabel>
          <div className="flex flex-wrap gap-2">
            {others.map((u) => (
              <PresenceChip key={u.userId} user={u} />
            ))}
          </div>
        </div>
      </>
    </TooltipProvider>
  );
}

function PresenceChip({ user }: { user: PresenceUser }) {
  const initial = user.email.slice(0, 1).toUpperCase();
  const isViewing = user.status === "viewing";

  return (
    <Tooltip>
      <TooltipTrigger asChild>
        <div
          className={cn(
            "flex items-center gap-1.5 rounded-full px-2 py-1 text-xs border transition-colors",
            isViewing
              ? "bg-primary/20 border-primary/40 text-foreground"
              : "bg-glass border-glass text-muted-foreground/60",
          )}
        >
          <span
            className={cn(
              "inline-flex h-5 w-5 items-center justify-center rounded-full text-[10px] font-medium",
              isViewing
                ? "bg-primary/40 text-white"
                : "bg-glass-strong text-muted-foreground/50",
            )}
          >
            {initial}
          </span>
          <span className="truncate max-w-[120px]">
            {user.email.split("@")[0]}
          </span>
          <span
            className={cn(
              "h-1.5 w-1.5 rounded-full shrink-0",
              isViewing ? "bg-green-400" : "bg-glass-strong",
            )}
          />
        </div>
      </TooltipTrigger>
      <TooltipContent side="bottom" className="text-xs">
        {user.email} — {isViewing ? "viewing now" : "opened recently"}
      </TooltipContent>
    </Tooltip>
  );
}
