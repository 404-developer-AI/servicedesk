import * as React from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import { agentQueueApi, taxonomyApi } from "@/lib/api";
import { useServerTime, toServerLocal, formatUtcSuffix } from "@/hooks/useServerTime";
import { AgentPicker } from "@/components/AgentPicker";
import { ContactFormDialog } from "@/components/ContactFormDialog";
import { CompanyEditDialog } from "@/components/CompanyEditDialog";
import { Button } from "@/components/ui/button";
import { cn } from "@/lib/utils";
import type { Ticket, TicketFieldUpdate, Contact, CompanyDetail } from "@/lib/ticket-api";
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
  Globe,
  Mail,
  Phone,
  MapPin,
  Briefcase,
  Pencil,
  User,
} from "lucide-react";

type TicketSidePanelProps = {
  ticket: Ticket;
  onUpdate: (fields: TicketFieldUpdate) => Promise<void>;
  onRequestCompanyAssign?: () => void;
};

type TabId = "status" | "contact" | "company";

const SELECT_CLASS =
  "w-full h-9 px-2 text-sm rounded-md border border-white/10 bg-white/[0.04] text-foreground outline-none focus:border-primary/60 cursor-pointer";

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

function ColorDot({ color }: { color: string }) {
  return (
    <span
      className="inline-block w-2 h-2 rounded-full shrink-0"
      style={{ backgroundColor: color || "#888" }}
    />
  );
}


function ServerDate({ iso }: { iso: string | null | undefined }) {
  const { time } = useServerTime();
  const offset = time?.offsetMinutes ?? 0;
  if (!iso) return <span>Not set</span>;
  return (
    <span>
      {toServerLocal(iso, offset)}{" "}
      <span className="text-muted-foreground/40 text-xs">{formatUtcSuffix(iso)}</span>
    </span>
  );
}

function SourceBadge({ source }: { source: string }) {
  return (
    <span className="inline-flex items-center rounded px-1.5 py-0.5 text-xs font-medium border border-white/10 bg-white/[0.05] text-muted-foreground">
      {source}
    </span>
  );
}

function EmptyState({ text }: { text: string }) {
  return (
    <div className="text-sm text-muted-foreground/60 italic py-4 text-center">
      {text}
    </div>
  );
}

export function TicketSidePanel({ ticket, onUpdate, onRequestCompanyAssign }: TicketSidePanelProps) {
  const [activeTab, setActiveTab] = React.useState<TabId>("status");

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
    <div className="glass-card w-[320px] shrink-0 flex flex-col min-h-0">
      {/* Tab bar */}
      <div className="flex border-b border-white/10 shrink-0">
        {tabs.map((tab) => (
          <button
            key={tab.id}
            type="button"
            onClick={() => setActiveTab(tab.id)}
            className={cn(
              "flex-1 px-2 py-2.5 text-xs font-medium truncate transition-colors relative",
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

      {/* Tab content */}
      <div className="p-4 space-y-4 flex-1 overflow-y-auto">
        {activeTab === "status" && (
          <StatusTab ticket={ticket} onUpdate={onUpdate} />
        )}
        {activeTab === "contact" && (
          <ContactTab contact={contact ?? null} />
        )}
        {activeTab === "company" && (
          <CompanyTab
            ticket={ticket}
            companyDetail={companyDetail ?? null}
            onRequestCompanyAssign={onRequestCompanyAssign}
          />
        )}
      </div>
    </div>
  );
}

/* ─── Status Tab ─── */

function StatusTab({
  ticket,
  onUpdate,
}: {
  ticket: Ticket;
  onUpdate: (fields: TicketFieldUpdate) => Promise<void>;
}) {
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
    queryKey: ["categories"],
    queryFn: taxonomyApi.categories.list,
    staleTime: 300_000,
  });

  const currentStatus = statuses?.find((s) => s.id === ticket.statusId);
  const currentPriority = priorities?.find((p) => p.id === ticket.priorityId);
  const currentQueue = queues?.find((q) => q.id === ticket.queueId);

  return (
    <>
      <div>
        <FieldLabel>Status</FieldLabel>
        <div className="relative">
          {currentStatus && (
            <span className="absolute left-2 top-1/2 -translate-y-1/2 pointer-events-none z-10">
              <ColorDot color={currentStatus.color} />
            </span>
          )}
          <select
            value={ticket.statusId}
            onChange={(e) => onUpdate({ statusId: e.target.value })}
            className={cn(SELECT_CLASS, currentStatus && "pl-6")}
          >
            {statuses?.map((s) => (
              <option key={s.id} value={s.id} className="bg-background">
                {s.name} ({s.stateCategory})
              </option>
            ))}
          </select>
        </div>
      </div>

      <div>
        <FieldLabel>Priority</FieldLabel>
        <div className="relative">
          {currentPriority && (
            <span className="absolute left-2 top-1/2 -translate-y-1/2 pointer-events-none z-10">
              <ColorDot color={currentPriority.color} />
            </span>
          )}
          <select
            value={ticket.priorityId}
            onChange={(e) => onUpdate({ priorityId: e.target.value })}
            className={cn(SELECT_CLASS, currentPriority && "pl-6")}
          >
            {priorities?.map((p) => (
              <option key={p.id} value={p.id} className="bg-background">
                {p.name}
              </option>
            ))}
          </select>
        </div>
      </div>

      <div>
        <FieldLabel>Queue</FieldLabel>
        <div className="relative">
          {currentQueue && (
            <span className="absolute left-2 top-1/2 -translate-y-1/2 pointer-events-none z-10">
              <ColorDot color={currentQueue.color} />
            </span>
          )}
          <select
            value={ticket.queueId}
            onChange={(e) => onUpdate({ queueId: e.target.value })}
            className={cn(SELECT_CLASS, currentQueue && "pl-6")}
          >
            {queues?.map((q) => (
              <option key={q.id} value={q.id} className="bg-background">
                {q.name}
              </option>
            ))}
          </select>
        </div>
      </div>

      <div>
        <FieldLabel>Category</FieldLabel>
        <select
          value={ticket.categoryId ?? ""}
          onChange={(e) =>
            onUpdate({ categoryId: e.target.value || undefined })
          }
          className={SELECT_CLASS}
        >
          <option value="" className="bg-background">
            None
          </option>
          {categories?.map((c) => (
            <option key={c.id} value={c.id} className="bg-background">
              {c.name}
            </option>
          ))}
        </select>
      </div>

      <div>
        <FieldLabel>Assignee</FieldLabel>
        <AgentPicker
          value={ticket.assigneeUserId}
          onChange={(userId) => onUpdate({ assigneeUserId: userId ?? undefined })}
        />
      </div>

      <div className="border-t border-white/10" />

      <div>
        <FieldLabel>Created</FieldLabel>
        <div className="text-sm text-foreground/80"><ServerDate iso={ticket.createdUtc} /></div>
      </div>

      <div>
        <FieldLabel>Updated</FieldLabel>
        <div className="text-sm text-foreground/80"><ServerDate iso={ticket.updatedUtc} /></div>
      </div>

      <div>
        <FieldLabel>Due</FieldLabel>
        <div className="text-sm text-foreground/80"><ServerDate iso={ticket.dueUtc} /></div>
      </div>

      <div>
        <FieldLabel>Source</FieldLabel>
        <SourceBadge source={ticket.source} />
      </div>

      <TicketPresence ticketId={ticket.id} />
    </>
  );
}

/* ─── Contact Tab ─── */

function ContactTab({ contact }: { contact: Contact | null }) {
  const qc = useQueryClient();
  const [editing, setEditing] = React.useState(false);

  if (!contact) return <EmptyState text="Loading contact..." />;

  const fullName =
    contact.firstName || contact.lastName
      ? `${contact.firstName} ${contact.lastName}`.trim()
      : null;

  return (
    <>
      <div className="flex items-center justify-between -mt-1 mb-1">
        <div className="text-[10px] uppercase tracking-wider text-muted-foreground/70">
          Requester
        </div>
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

      <ContactFormDialog
        open={editing}
        mode="edit"
        initial={contact}
        onClose={() => setEditing(false)}
        onSaved={() => {
          qc.invalidateQueries({ queryKey: ["contact", contact.id] });
        }}
      />

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

      <div className="border-t border-white/10" />

      <div>
        <FieldLabel>Status</FieldLabel>
        <span
          className={cn(
            "inline-flex items-center rounded px-1.5 py-0.5 text-xs font-medium border",
            contact.isActive
              ? "border-green-500/30 bg-green-500/10 text-green-400"
              : "border-white/10 bg-white/[0.05] text-muted-foreground",
          )}
        >
          {contact.isActive ? "Active" : "Inactive"}
        </span>
      </div>

      <div>
        <FieldLabel>Created</FieldLabel>
        <div className="text-sm text-foreground/80"><ServerDate iso={contact.createdUtc} /></div>
      </div>
    </>
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
          <div className="border-t border-white/10" />
          <div>
            <FieldLabel>Domains</FieldLabel>
            <div className="flex flex-wrap gap-1.5">
              {domains.map((d) => (
                <span
                  key={d.id}
                  className="inline-flex items-center gap-1 rounded px-1.5 py-0.5 text-xs border border-white/10 bg-white/[0.05] text-muted-foreground"
                >
                  <Globe className="h-3 w-3" />
                  {d.domain}
                </span>
              ))}
            </div>
          </div>
        </>
      )}

      <div className="border-t border-white/10" />

      <div>
        <FieldLabel>Status</FieldLabel>
        <span
          className={cn(
            "inline-flex items-center rounded px-1.5 py-0.5 text-xs font-medium border",
            company.isActive
              ? "border-green-500/30 bg-green-500/10 text-green-400"
              : "border-white/10 bg-white/[0.05] text-muted-foreground",
          )}
        >
          {company.isActive ? "Active" : "Inactive"}
        </span>
      </div>
    </>
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
        <div className="border-t border-white/10" />
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
              : "bg-white/[0.04] border-white/10 text-muted-foreground/60",
          )}
        >
          <span
            className={cn(
              "inline-flex h-5 w-5 items-center justify-center rounded-full text-[10px] font-medium",
              isViewing
                ? "bg-primary/40 text-white"
                : "bg-white/[0.08] text-muted-foreground/50",
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
              isViewing ? "bg-green-400" : "bg-white/20",
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
