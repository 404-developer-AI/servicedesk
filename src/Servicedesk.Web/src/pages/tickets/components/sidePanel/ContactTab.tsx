import * as React from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import { ContactFormDialog } from "@/components/ContactFormDialog";
import { AddContactLinkDialog } from "@/components/AddContactLinkDialog";
import { SwitchRequesterDialog } from "@/components/SwitchRequesterDialog";
import { Button } from "@/components/ui/button";
import { cn } from "@/lib/utils";
import type { Contact, ContactCompanyOption } from "@/lib/companies-api";
import type { ContactCompanyRole } from "@/lib/ticket-api";
import { contactApi } from "@/lib/companies-api";
import { Building2, Mail, Phone, Smartphone, Briefcase, Pencil, Plus, User, UserCog } from "lucide-react";
import { LINK_ROLE_BADGE } from "./linkRoleBadge";
import { FieldLabel, FieldRow, ServerDate, EmptyState } from "./fields";

/* ─── Contact Tab ─── */

export function ContactTab({
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

      {contact.mobilePhone && (
        <FieldRow icon={Smartphone} label="Mobile">
          <a
            href={`tel:${contact.mobilePhone}`}
            className="text-primary hover:underline"
          >
            {contact.mobilePhone}
          </a>
        </FieldRow>
      )}

      {contact.jobTitle && (
        <FieldRow icon={Briefcase} label="Job title">
          {contact.jobTitle}
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
