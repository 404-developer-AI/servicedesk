import * as React from "react";
import { useQuery } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import { AddCompanyContactDialog } from "@/components/AddCompanyContactDialog";
import { CompanyEditDialog } from "@/components/CompanyEditDialog";
import { Button } from "@/components/ui/button";
import { cn } from "@/lib/utils";
import type { Ticket, ContactCompanyRole } from "@/lib/ticket-api";
import type { Contact, CompanyDetail, ContactCompanyLink } from "@/lib/companies-api";
import { companyApi } from "@/lib/companies-api";
import { Building2, Globe, Mail, Phone, MapPin, Pencil, Plus, User } from "lucide-react";
import { FieldLabel, FieldRow, EmptyState } from "./fields";
import { LINK_ROLE_BADGE } from "./linkRoleBadge";

/* ─── Company Tab ─── */

const RESOLVED_VIA_LABEL: Record<NonNullable<Ticket["companyResolvedVia"]>, string> = {
  thread_reply: "via thread-reply",
  primary: "via primary link",
  secondary: "via secondary link",
  manual: "manually assigned",
  unresolved: "not resolved",
};

export function ResolutionBadge({
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

export function CompanyTab({
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
