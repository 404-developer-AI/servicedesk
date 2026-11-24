import * as React from "react";
import { useQuery } from "@tanstack/react-query";
import { agentQueueApi } from "@/lib/queue-access-api";
import { settingsApi } from "@/lib/settings-api";
import { taxonomyApi } from "@/lib/taxonomy-api";
import { AgentPicker } from "@/components/AgentPicker";
import { useProjectSettings } from "@/pages/tickets/components/projects/ProjectPromptDialog";
import { SyncOrdersButton } from "@/pages/orders/SyncOrdersButton";
import { TaxonomySelect } from "@/components/TaxonomySelect";
import { PendingTillField } from "@/components/PendingTillField";
import { Button } from "@/components/ui/button";
import { flagColors, useTicketFlagSettings } from "@/lib/ticketFlags";
import { FlagToggleRow } from "@/components/TicketFlagToggle";
import { cn } from "@/lib/utils";
import type { TicketFormSummary } from "@/lib/forms-api";
import type { Ticket, TicketFieldUpdate } from "@/lib/ticket-api";
import type { CompanyDetail } from "@/lib/companies-api";
import { contactApi } from "@/lib/companies-api";
import { useAuth } from "@/auth/authStore";
import { Building2, Eye, EyeOff, GitMerge, Pencil } from "lucide-react";
import { FieldLabel, SoftLockedField } from "./fields";
import { withInactiveCarveOut } from "./withInactiveCarveOut";
import { FormsBlock, OrdersBlock, ZammadImportBadge } from "./StatusBlocks";
import { RelationshipsBlock } from "./RelationshipsBlock";
import { ResolutionBadge } from "./CompanyTab";

/* ─── Status Tab ─── */

export function StatusTab({
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
  projectsEnabled,
  projectTicketNumber,
  projectLinkedByUserName,
  projectLinkedTicketCount,
  contactNotLinkedWarning,
  taggedOrders,
  ticketForms,
  softLocked,
  onRequestLinkProject,
  onUnlinkProject,
  onRevertProject,
}: {
  softLocked: boolean;
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
  projectsEnabled: boolean;
  projectTicketNumber: string | null;
  projectLinkedByUserName: string | null;
  projectLinkedTicketCount: number;
  contactNotLinkedWarning?: boolean;
  taggedOrders: { id: string; label: string }[];
  ticketForms: TicketFormSummary[];
  onRequestLinkProject: () => void;
  onUnlinkProject: () => void;
  onRevertProject: () => void;
}) {
  // v0.0.59 — "Sync orders" button is shown only to users with the Orders
  // feature flag.
  const { user } = useAuth();
  // v0.0.105 — with a configured project queue, project tickets hide the
  // queue selector entirely (the server refuses moves regardless).
  const projectSettingsQ = useProjectSettings();
  const projectQueuePinned = !!projectSettingsQ.data?.queueId;
  const { data: flagSettings } = useTicketFlagSettings();
  const flagColor = flagColors(flagSettings);
  // Pulsing "Contact not linked" warning. Only renders when (a) the admin
  // has the toggle on and (b) the requester has zero current company links.
  // v0.1.27 — agent-readable endpoint (the admin-only settings list 403'd
  // for agents, so they never saw this warning).
  const { data: warningSetting } = useQuery({
    queryKey: ["settings", "ticket-warnings"],
    queryFn: () => settingsApi.ticketWarnings(),
    staleTime: 5 * 60_000, // v0.1.25 — global config, not per ticket
  });
  const showContactNotLinkedSetting = warningSetting?.showContactNotLinked ?? false;
  const { data: requesterLinks } = useQuery({
    queryKey: ["contact-companies", ticket.requesterContactId],
    queryFn: () => contactApi.listCompanies(ticket.requesterContactId),
    staleTime: 60_000,
    enabled: showContactNotLinkedSetting && !!ticket.requesterContactId,
  });
  // v0.1.33 — until both lookups are in, the detail's own answer decides,
  // so the banner is there on the first paint instead of pushing the
  // cards below it down a moment later.
  const showContactNotLinked =
    warningSetting !== undefined && (requesterLinks !== undefined || !showContactNotLinkedSetting)
      ? showContactNotLinkedSetting && (requesterLinks?.length ?? null) === 0
      : (contactNotLinkedWarning ?? false);

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

  const { data: categories } = useQuery({
    queryKey: ["taxonomy", "categories"],
    queryFn: taxonomyApi.categories.list,
    staleTime: 300_000,
  });
  const currentCategory = categories?.find((c) => c.id === ticket.categoryId);
  const categoryOptions = React.useMemo(
    () => withInactiveCarveOut(categories, currentCategory),
    [categories, currentCategory],
  );

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
  const hasProjectRelation =
    projectsEnabled && (!!ticket.projectTicketId || ticket.isProject);
  const showRelationships =
    hasMergeRelations || hasParentRelation || hasChildren || hasProjectRelation;

  return (
    <>
      {user?.adsolutOrdersEnabled && <SyncOrdersButton />}
      {showContactNotLinked && (
        <div
          className="flex items-start gap-2 rounded-md border border-amber-400/70 bg-amber-100/80 px-2.5 py-1.5 text-xs text-amber-800 animate-pulse dark:border-amber-500/40 dark:bg-amber-500/10 dark:text-amber-200"
          title="The requester is not linked to any company. Open the Contact tab to add a company link."
        >
          <span
            aria-hidden
            className="mt-1 inline-block h-1.5 w-1.5 shrink-0 rounded-full bg-amber-500 shadow-[0_0_8px_rgba(251,191,36,0.6)] dark:bg-amber-400"
          />
          <div className="min-w-0">
            <div className="font-medium">Contact not linked</div>
            <div className="text-[11px] text-amber-700 dark:text-amber-300/80">Please link to a company</div>
          </div>
        </div>
      )}
      <SoftLockedField locked={softLocked}>
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
      </SoftLockedField>

      {currentStatus?.stateCategory === "Pending" && (
        <SoftLockedField locked={softLocked}>
          <FieldLabel>Pending till</FieldLabel>
          <PendingTillField
            value={ticket.pendingTillUtc}
            onCommit={(iso) => onUpdate({ pendingTillUtc: iso })}
          />
        </SoftLockedField>
      )}

      {/* v0.0.105 — project tickets pinned to the project queue have no
          queue selector: the queue is an implementation detail there and
          the server refuses moves anyway. */}
      {!(projectsEnabled && ticket.isProject && projectQueuePinned) && (
        <SoftLockedField locked={softLocked}>
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
        </SoftLockedField>
      )}

      <SoftLockedField locked={softLocked}>
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
      </SoftLockedField>

      {/* v0.1.61 — category marks the phase of a ticket (view tabs, soft
          lock). Hidden while no categories exist. */}
      {(categories?.length ?? 0) > 0 && (
        <SoftLockedField locked={softLocked}>
          <FieldLabel>Category</FieldLabel>
          <TaxonomySelect
            value={ticket.categoryId ?? ""}
            onChange={(v) => onUpdate(v ? { categoryId: v } : { clearCategory: true })}
            allowEmpty
            emptyLabel="No category"
            placeholder="No category"
            options={categoryOptions.map((c) => ({
              id: c.id,
              name: c.name,
              color: c.isLocked ? "var(--primary)" : "",
              suffix: !c.isActive ? "— inactive" : c.isLocked ? "· locked" : undefined,
            }))}
          />
        </SoftLockedField>
      )}

      {/* v0.1.17 — Call-back / Research flags. Internal only; each toggle is
          a TicketFlagChange timeline event and feeds the view floats. */}
      <div className="space-y-1.5">
        <FlagToggleRow
          label="Call-back ticket"
          hint="Someone has to call this customer back. Floats in views with the call-back float on."
          color={flagColor.callback}
          checked={ticket.isCallback}
          onChange={(next) => onUpdate({ isCallback: next })}
        />
        <FlagToggleRow
          label="Research ticket"
          hint="This ticket is under investigation. Floats in views with the research float on."
          color={flagColor.research}
          checked={ticket.isResearch}
          onChange={(next) => onUpdate({ isResearch: next })}
        />
      </div>

      <SoftLockedField locked={softLocked}>
        <FieldLabel>Assignee</FieldLabel>
        <AgentPicker
          value={ticket.assigneeUserId}
          onChange={(userId) =>
            onUpdate(userId ? { assigneeUserId: userId } : { unassignAssignee: true })
          }
        />
      </SoftLockedField>

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
        projectsEnabled={projectsEnabled}
        projectTicketNumber={projectTicketNumber}
        projectLinkedByUserName={projectLinkedByUserName}
        projectLinkedTicketCount={projectLinkedTicketCount}
        onRequestLinkProject={onRequestLinkProject}
        onUnlinkProject={onUnlinkProject}
        onRevertProject={onRevertProject}
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

      {/* Orders tagged via "::" pills — same feature gate as the pills
          themselves (OrderPillHost is only mounted for these users, so
          rows would be dead buttons without it). Hidden when nothing is
          tagged. */}
      {user?.adsolutOrdersEnabled && taggedOrders.length > 0 && (
        <OrdersBlock orders={taggedOrders} />
      )}

      {ticketForms.length > 0 && <FormsBlock forms={ticketForms} />}

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
                ? "border-primary/40 bg-primary/8 text-foreground"
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
    </>
  );
}
