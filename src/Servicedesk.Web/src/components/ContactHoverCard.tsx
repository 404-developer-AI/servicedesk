import * as React from "react";
import { useQuery } from "@tanstack/react-query";
import { Briefcase, Building2, Mail, Phone, Smartphone, UserRound } from "lucide-react";
import { contactApi } from "@/lib/ticket-api";
import { HoverCard, HoverCardTrigger, HoverCardContent } from "@/components/ui/hover-card";
import { CopyButton } from "@/components/CopyButton";
import { Skeleton } from "@/components/ui/skeleton";

// Hover intent delays — long enough that sweeping the cursor across a list
// doesn't flash cards, short enough to feel instant on a deliberate hover.
const OPEN_DELAY_MS = 350;
const CLOSE_DELAY_MS = 150;

/// Wraps a contact name anywhere in the UI (search hits, ticket rows) with
/// a hover preview card: name, role, company, and copyable email / phone —
/// so an agent can grab a contact detail without leaving the ticket or
/// search they're in. Clicking the wrapped element behaves as before; the
/// card itself never steals focus (the global-search dropdown closes on
/// input blur) and swallows clicks so a copy tap doesn't navigate the row.
export function ContactHoverCard({
  contactId,
  children,
}: {
  contactId: string;
  children: React.ReactNode;
}) {
  return (
    <HoverCard openDelay={OPEN_DELAY_MS} closeDelay={CLOSE_DELAY_MS}>
      <HoverCardTrigger asChild>{children}</HoverCardTrigger>
      <HoverCardContent
        className="w-80 p-0"
        onMouseDown={(e) => e.preventDefault()}
        onClick={(e) => e.stopPropagation()}
      >
        <ContactHoverCardBody contactId={contactId} />
      </HoverCardContent>
    </HoverCard>
  );
}

/// Card body is a separate component so the queries only run once the card
/// actually opens (Radix mounts content lazily). Query keys match the
/// ticket side panel's, so a contact the agent already looked at renders
/// from cache with zero network traffic.
function ContactHoverCardBody({ contactId }: { contactId: string }) {
  const { data: contact } = useQuery({
    queryKey: ["contact", contactId],
    queryFn: () => contactApi.get(contactId),
    staleTime: 300_000,
  });
  const { data: companyLinks } = useQuery({
    queryKey: ["contact-companies", contactId],
    queryFn: () => contactApi.listCompanies(contactId),
    staleTime: 300_000,
  });

  if (!contact) {
    return (
      <div className="space-y-2 p-3">
        <Skeleton className="h-4 w-40" />
        <Skeleton className="h-3 w-28" />
        <Skeleton className="h-3 w-48" />
      </div>
    );
  }

  const fullName =
    `${contact.firstName} ${contact.lastName}`.trim() || contact.email;
  const primaryCompany =
    (companyLinks ?? []).find((l) => l.role === "primary")?.companyName ?? null;

  return (
    <div className="p-3">
      <div className="flex items-start gap-2.5">
        <div className="flex h-8 w-8 shrink-0 items-center justify-center rounded-full border border-glass bg-glass">
          <UserRound className="h-4 w-4 text-muted-foreground" />
        </div>
        <div className="min-w-0">
          <div className="flex items-center gap-2">
            <span className="truncate text-sm font-medium">{fullName}</span>
            {!contact.isActive && (
              <span className="shrink-0 rounded-full border border-glass px-1.5 py-0.5 text-[10px] uppercase tracking-wider text-muted-foreground">
                Inactive
              </span>
            )}
          </div>
          {(contact.jobTitle || primaryCompany) && (
            <div className="mt-0.5 flex flex-col gap-0.5 text-xs text-muted-foreground">
              {contact.jobTitle && (
                <span className="flex items-center gap-1.5 truncate">
                  <Briefcase className="h-3 w-3 shrink-0" />
                  {contact.jobTitle}
                </span>
              )}
              {primaryCompany && (
                <span className="flex items-center gap-1.5 truncate">
                  <Building2 className="h-3 w-3 shrink-0" />
                  {primaryCompany}
                </span>
              )}
            </div>
          )}
        </div>
      </div>

      <div className="mt-2.5 space-y-0.5 border-t border-glass pt-2.5">
        {contact.email && (
          <DetailRow
            icon={Mail}
            value={contact.email}
            href={`mailto:${contact.email}`}
            copyLabel="Copy email"
          />
        )}
        {contact.phone && (
          <DetailRow
            icon={Phone}
            value={contact.phone}
            href={`tel:${contact.phone}`}
            copyLabel="Copy phone"
          />
        )}
        {contact.mobilePhone && (
          <DetailRow
            icon={Smartphone}
            value={contact.mobilePhone}
            href={`tel:${contact.mobilePhone}`}
            copyLabel="Copy mobile"
          />
        )}
        {!contact.email && !contact.phone && !contact.mobilePhone && (
          <div className="text-xs text-muted-foreground">No contact details.</div>
        )}
      </div>
    </div>
  );
}

function DetailRow({
  icon: Icon,
  value,
  href,
  copyLabel,
}: {
  icon: React.ComponentType<{ className?: string }>;
  value: string;
  href: string;
  copyLabel: string;
}) {
  return (
    <div className="group flex items-center gap-2">
      <Icon className="h-3.5 w-3.5 shrink-0 text-muted-foreground" />
      <a
        href={href}
        className="min-w-0 flex-1 truncate text-xs text-foreground/90 hover:text-primary hover:underline"
        title={value}
      >
        {value}
      </a>
      <CopyButton
        value={value}
        label={copyLabel}
        className="opacity-0 transition-opacity group-hover:opacity-100 focus-visible:opacity-100"
      />
    </div>
  );
}
