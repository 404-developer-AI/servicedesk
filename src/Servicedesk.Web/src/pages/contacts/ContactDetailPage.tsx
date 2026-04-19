import * as React from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import { toast } from "sonner";
import {
  ArrowLeft,
  ArrowRight,
  Building2,
  Clock,
  History,
  Mail,
  MoveRight,
  Pencil,
  Plus,
  Phone as PhoneIcon,
  ShieldCheck,
  UserRound,
} from "lucide-react";
import { ApiError } from "@/lib/api";
import { useAuth } from "@/auth/authStore";
import {
  companyApi,
  contactApi,
  type Contact,
  type ContactAuditEntry,
  type ContactCompanyOption,
  type ContactCompanyRole,
  type ContactInput,
  type CompanyPickerItem,
} from "@/lib/ticket-api";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { AddContactLinkDialog } from "@/components/AddContactLinkDialog";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { Switch } from "@/components/ui/switch";
import { cn } from "@/lib/utils";

const ROLE_BADGE: Record<ContactCompanyRole, { label: string; className: string }> = {
  primary: {
    label: "Primary",
    className: "border-purple-400/50 bg-purple-500/20 text-purple-100",
  },
  secondary: {
    label: "Secondary",
    className: "border-sky-400/30 bg-sky-500/15 text-sky-200",
  },
  supplier: {
    label: "Supplier",
    className: "border-amber-400/30 bg-amber-500/15 text-amber-200",
  },
};

type Props = { contactId: string };

// Agents reach this page via the ticket side-panel or global search, but
// the contacts overview (/settings/contacts) is admin-only — so the "Back"
// link target depends on the viewer's role. Admins return to the overview;
// agents return to the ticket list (their primary working surface).
function BackLink({
  isAdmin,
  className,
  children,
}: {
  isAdmin: boolean;
  className: string;
  children: React.ReactNode;
}) {
  return isAdmin ? (
    <Link to="/settings/contacts" className={className}>
      {children}
    </Link>
  ) : (
    <Link to="/tickets" className={className}>
      {children}
    </Link>
  );
}

export function ContactDetailPage({ contactId }: Props) {
  const { user } = useAuth();
  const isAdmin = user?.role === "Admin";
  const backLabel = isAdmin ? "Back to contacts" : "Back";

  const { data: contact, isLoading } = useQuery({
    queryKey: ["contact", contactId],
    queryFn: () => contactApi.get(contactId),
  });

  const { data: companies, isLoading: loadingCompanies } = useQuery({
    queryKey: ["contact-companies", contactId],
    queryFn: () => contactApi.listCompanies(contactId),
  });

  const { data: audit, isLoading: loadingAudit } = useQuery({
    queryKey: ["contact-audit", contactId],
    queryFn: () => contactApi.audit(contactId, undefined, 50),
  });

  if (isLoading) {
    return (
      <div className="flex flex-col gap-4">
        <Skeleton className="h-10 w-64" />
        <Skeleton className="h-32 w-full" />
        <Skeleton className="h-48 w-full" />
      </div>
    );
  }
  if (!contact) {
    return (
      <div className="rounded-md border border-white/10 bg-white/[0.04] p-6 text-center text-sm text-muted-foreground">
        Contact not found.{" "}
        <BackLink isAdmin={isAdmin} className="text-primary hover:underline">
          {backLabel}
        </BackLink>
      </div>
    );
  }

  return (
    <div className="flex flex-col gap-6">
      <header className="flex flex-wrap items-start justify-between gap-3">
        <div className="flex flex-col gap-2">
          <BackLink
            isAdmin={isAdmin}
            className="inline-flex items-center gap-1 text-xs text-muted-foreground hover:text-foreground"
          >
            <ArrowLeft className="h-3 w-3" /> {backLabel}
          </BackLink>
          <h1 className="text-display-md font-semibold text-foreground">
            {[contact.firstName, contact.lastName].filter(Boolean).join(" ").trim() ||
              contact.email}
          </h1>
          <div className="flex flex-wrap items-center gap-3 text-sm text-muted-foreground">
            <span className="inline-flex items-center gap-1">
              <Mail className="h-3.5 w-3.5" /> {contact.email}
            </span>
            {contact.phone && (
              <span className="inline-flex items-center gap-1">
                <PhoneIcon className="h-3.5 w-3.5" /> {contact.phone}
              </span>
            )}
            {contact.jobTitle && (
              <span className="inline-flex items-center gap-1">
                <ShieldCheck className="h-3.5 w-3.5" /> {contact.jobTitle}
              </span>
            )}
            {!contact.isActive && (
              <Badge className="border border-white/10 bg-white/[0.05] text-[10px] font-normal text-muted-foreground">
                inactive
              </Badge>
            )}
          </div>
        </div>
      </header>

      <div className="grid gap-6 lg:grid-cols-[2fr_3fr]">
        <ContactEditCard contact={contact} />
        <CompanyLinksCard
          contactId={contact.id}
          companies={companies ?? []}
          loading={loadingCompanies}
        />
      </div>

      <AuditHistoryCard audit={audit?.items ?? []} loading={loadingAudit} />
    </div>
  );
}

function ContactEditCard({ contact }: { contact: Contact }) {
  const qc = useQueryClient();
  const [editing, setEditing] = React.useState(false);
  const [form, setForm] = React.useState<ContactInput>(() => ({
    email: contact.email,
    firstName: contact.firstName,
    lastName: contact.lastName,
    phone: contact.phone,
    jobTitle: contact.jobTitle,
    isActive: contact.isActive,
  }));

  React.useEffect(() => {
    if (!editing) {
      setForm({
        email: contact.email,
        firstName: contact.firstName,
        lastName: contact.lastName,
        phone: contact.phone,
        jobTitle: contact.jobTitle,
        isActive: contact.isActive,
      });
    }
  }, [contact, editing]);

  const save = useMutation({
    mutationFn: () => contactApi.update(contact.id, form),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["contact", contact.id] });
      qc.invalidateQueries({ queryKey: ["contact-audit", contact.id] });
      qc.invalidateQueries({ queryKey: ["contacts"] });
      toast.success("Contact updated");
      setEditing(false);
    },
    onError: (err) => {
      if (err instanceof ApiError && err.status === 400) {
        toast.error("Check the fields and try again.");
      } else {
        toast.error("Update failed");
      }
    },
  });

  return (
    <section className="glass-card p-5">
      <div className="flex items-center justify-between gap-2">
        <h2 className="text-sm font-medium uppercase tracking-wide text-muted-foreground">
          Contact details
        </h2>
        {!editing && (
          <Button variant="ghost" size="sm" onClick={() => setEditing(true)}>
            <Pencil className="h-3.5 w-3.5" /> Edit
          </Button>
        )}
      </div>
      {editing ? (
        <div className="mt-3 space-y-3">
          <div className="grid grid-cols-2 gap-3">
            <Field label="First name">
              <Input
                value={form.firstName ?? ""}
                onChange={(e) => setForm((f) => ({ ...f, firstName: e.target.value }))}
              />
            </Field>
            <Field label="Last name">
              <Input
                value={form.lastName ?? ""}
                onChange={(e) => setForm((f) => ({ ...f, lastName: e.target.value }))}
              />
            </Field>
          </div>
          <Field label="Email *">
            <Input
              type="email"
              value={form.email}
              onChange={(e) => setForm((f) => ({ ...f, email: e.target.value }))}
            />
          </Field>
          <div className="grid grid-cols-2 gap-3">
            <Field label="Phone">
              <Input
                value={form.phone ?? ""}
                onChange={(e) => setForm((f) => ({ ...f, phone: e.target.value }))}
              />
            </Field>
            <Field label="Job title">
              <Input
                value={form.jobTitle ?? ""}
                onChange={(e) => setForm((f) => ({ ...f, jobTitle: e.target.value }))}
              />
            </Field>
          </div>
          <label className="flex items-center justify-between gap-3 text-sm">
            <span>Active</span>
            <Switch
              checked={!!form.isActive}
              onCheckedChange={(v) => setForm((f) => ({ ...f, isActive: v }))}
            />
          </label>
          <div className="flex justify-end gap-2 pt-2">
            <Button variant="ghost" size="sm" onClick={() => setEditing(false)}>
              Cancel
            </Button>
            <Button size="sm" onClick={() => save.mutate()} disabled={save.isPending}>
              {save.isPending ? "Saving…" : "Save"}
            </Button>
          </div>
        </div>
      ) : (
        <dl className="mt-3 grid grid-cols-2 gap-x-6 gap-y-3 text-sm">
          <DefinitionRow label="First name" value={contact.firstName} />
          <DefinitionRow label="Last name" value={contact.lastName} />
          <DefinitionRow label="Phone" value={contact.phone} />
          <DefinitionRow label="Job title" value={contact.jobTitle} />
          <DefinitionRow
            label="Member role"
            value={contact.companyRole}
            hint="Used later by the customer portal (Member / TicketManager)."
          />
          <DefinitionRow
            label="Status"
            value={contact.isActive ? "Active" : "Inactive"}
          />
        </dl>
      )}
    </section>
  );
}

function CompanyLinksCard({
  contactId,
  companies,
  loading,
}: {
  contactId: string;
  companies: ContactCompanyOption[];
  loading: boolean;
}) {
  const [movingPrimary, setMovingPrimary] = React.useState(false);
  const [linking, setLinking] = React.useState(false);
  const primary = companies.find((c) => c.role === "primary") ?? null;
  const secondary = companies.filter((c) => c.role === "secondary");
  const supplier = companies.filter((c) => c.role === "supplier");
  const existingCompanyIds = React.useMemo(
    () => new Set(companies.map((c) => c.companyId)),
    [companies],
  );

  return (
    <section className="glass-card p-5">
      <div className="flex items-center justify-between gap-2">
        <h2 className="text-sm font-medium uppercase tracking-wide text-muted-foreground">
          Company links
        </h2>
        <div className="flex items-center gap-2">
          <Button
            variant="ghost"
            size="sm"
            onClick={() => setLinking(true)}
            className="text-xs"
          >
            <Plus className="h-3.5 w-3.5" /> Link company
          </Button>
          {primary && (
            <Button
              variant="ghost"
              size="sm"
              onClick={() => setMovingPrimary(true)}
              className="text-xs"
            >
              <MoveRight className="h-3.5 w-3.5" /> Move primary
            </Button>
          )}
          <Badge className="border border-white/10 bg-white/[0.05] text-[10px] font-normal text-muted-foreground">
            {companies.length} total
          </Badge>
        </div>
      </div>
      {loading ? (
        <div className="mt-3 space-y-2">
          <Skeleton className="h-10 w-full" />
          <Skeleton className="h-10 w-full" />
        </div>
      ) : companies.length === 0 ? (
        <p className="mt-3 text-sm text-muted-foreground">
          This contact has no company links yet. Link them from a company's Contacts tab
          to surface their tickets under that company.
        </p>
      ) : (
        <div className="mt-3 space-y-3">
          <RoleGroup title="Primary" items={primary ? [primary] : []} />
          <RoleGroup title="Secondary" items={secondary} />
          <RoleGroup title="Supplier" items={supplier} />
        </div>
      )}
      {primary && (
        <p className="mt-4 rounded-md border border-white/10 bg-white/[0.03] p-3 text-xs text-muted-foreground">
          Moving the primary link demotes the current primary to <em>secondary</em> in
          one atomic step. Tickets created before the move stay on the old company —
          <code className="ml-1 font-mono text-[10px]">tickets.company_id</code> is
          frozen at intake.
        </p>
      )}

      {movingPrimary && primary && (
        <MovePrimaryDialog
          contactId={contactId}
          current={primary}
          onClose={() => setMovingPrimary(false)}
        />
      )}

      {linking && (
        <AddContactLinkDialog
          contactId={contactId}
          existingCompanyIds={existingCompanyIds}
          currentPrimary={primary}
          onClose={() => setLinking(false)}
        />
      )}
    </section>
  );
}

function MovePrimaryDialog({
  contactId,
  current,
  onClose,
}: {
  contactId: string;
  current: ContactCompanyOption;
  onClose: () => void;
}) {
  const qc = useQueryClient();
  const [search, setSearch] = React.useState("");
  const [debouncedSearch, setDebouncedSearch] = React.useState("");
  const [target, setTarget] = React.useState<CompanyPickerItem | null>(null);

  React.useEffect(() => {
    const timer = setTimeout(() => setDebouncedSearch(search), 200);
    return () => clearTimeout(timer);
  }, [search]);

  const { data: matches } = useQuery({
    queryKey: ["companies", "picker", debouncedSearch],
    queryFn: () => companyApi.picker(debouncedSearch || undefined),
    placeholderData: (prev) => prev,
  });

  const move = useMutation({
    mutationFn: async () => {
      if (!target) throw new Error("Pick a target company first.");
      // Re-use the existing link endpoint with role='primary'. The server
      // atomically demotes the current primary and emits `contact.primary.moved`
      // when the target differs from the source — no separate "move" route.
      await companyApi.linkContact(target.id, contactId, "primary");
    },
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["contact", contactId] });
      qc.invalidateQueries({ queryKey: ["contact-companies", contactId] });
      qc.invalidateQueries({ queryKey: ["contact-audit", contactId] });
      qc.invalidateQueries({ queryKey: ["contacts"] });
      toast.success(`Primary moved to ${target?.name}`);
      onClose();
    },
    onError: () => toast.error("Could not move primary"),
  });

  const sameAsCurrent = target?.id === current.companyId;

  return (
    <Dialog open onOpenChange={(v) => (!v ? onClose() : null)}>
      <DialogContent className="max-w-lg">
        <DialogHeader>
          <DialogTitle>Move primary link</DialogTitle>
          <DialogDescription>
            The current primary becomes secondary. Historic tickets keep their
            original company assignment.
          </DialogDescription>
        </DialogHeader>

        <div className="space-y-3">
          <div className="rounded-md border border-white/10 bg-white/[0.03] p-3 text-sm">
            <div className="text-[10px] uppercase tracking-wide text-muted-foreground">
              From
            </div>
            <div className="mt-1 flex items-center gap-2">
              <Building2 className="h-3.5 w-3.5 text-muted-foreground" />
              <span>{current.companyShortName || current.companyName}</span>
              <span className="font-mono text-[10px] text-muted-foreground">
                {current.companyCode}
              </span>
            </div>
          </div>

          <div>
            <div className="mb-1 text-[10px] uppercase tracking-wide text-muted-foreground">
              To
            </div>
            <Input
              autoFocus
              placeholder="Search companies…"
              value={search}
              onChange={(e) => setSearch(e.target.value)}
            />
            <div className="mt-2 max-h-[220px] space-y-1 overflow-auto rounded-md border border-white/5 bg-white/[0.02] p-1">
              {(matches ?? []).length === 0 ? (
                <div className="px-3 py-3 text-xs text-muted-foreground">No matches.</div>
              ) : (
                (matches ?? []).map((c) => {
                  const isCurrent = c.id === current.companyId;
                  const selected = target?.id === c.id;
                  return (
                    <button
                      key={c.id}
                      type="button"
                      onClick={() => setTarget(c)}
                      disabled={isCurrent}
                      className={cn(
                        "flex w-full items-center justify-between gap-2 rounded-md px-2 py-1.5 text-left text-sm",
                        selected
                          ? "border border-purple-400/50 bg-purple-500/10"
                          : "hover:bg-white/[0.05]",
                        isCurrent && "cursor-not-allowed opacity-40",
                      )}
                    >
                      <span className="flex items-center gap-2 truncate">
                        <Building2 className="h-3.5 w-3.5 text-muted-foreground" />
                        <span className="truncate">{c.shortName || c.name}</span>
                        <span className="font-mono text-[10px] text-muted-foreground">
                          {c.code}
                        </span>
                      </span>
                      {isCurrent && (
                        <span className="text-[10px] text-muted-foreground">current</span>
                      )}
                    </button>
                  );
                })
              )}
            </div>
          </div>

          {sameAsCurrent && (
            <p className="text-xs text-amber-300">
              Target is the same as the current primary — nothing to do.
            </p>
          )}
        </div>

        <DialogFooter>
          <Button variant="ghost" onClick={onClose} disabled={move.isPending}>
            Cancel
          </Button>
          <Button
            onClick={() => move.mutate()}
            disabled={!target || sameAsCurrent || move.isPending}
          >
            {move.isPending ? "Moving…" : "Move primary"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

function RoleGroup({
  title,
  items,
}: {
  title: string;
  items: ContactCompanyOption[];
}) {
  if (items.length === 0) return null;
  const role = title.toLowerCase() as ContactCompanyRole;
  const badge = ROLE_BADGE[role];
  return (
    <div>
      <div className="mb-1.5 flex items-center gap-2 text-[10px] font-medium uppercase tracking-wide text-muted-foreground">
        <Badge className={cn("border text-[10px] font-normal", badge?.className)}>
          {title}
        </Badge>
      </div>
      <ul className="space-y-1.5">
        {items.map((c) => (
          <li key={c.linkId}>
            <Link
              to="/companies/$companyId"
              params={{ companyId: c.companyId }}
              className="group flex items-center justify-between gap-3 rounded-md border border-white/5 bg-white/[0.02] px-3 py-2 text-sm transition-colors hover:border-white/10 hover:bg-white/[0.05]"
            >
              <span className="flex items-center gap-2">
                <Building2 className="h-3.5 w-3.5 text-muted-foreground" />
                <span>{c.companyShortName || c.companyName}</span>
                <span className="font-mono text-[10px] text-muted-foreground">
                  {c.companyCode}
                </span>
                {!c.companyIsActive && (
                  <Badge className="border border-white/10 bg-white/[0.05] text-[10px] font-normal text-muted-foreground">
                    inactive
                  </Badge>
                )}
              </span>
              <ArrowRight className="h-3.5 w-3.5 text-muted-foreground opacity-0 transition-opacity group-hover:opacity-100" />
            </Link>
          </li>
        ))}
      </ul>
    </div>
  );
}

function AuditHistoryCard({
  audit,
  loading,
}: {
  audit: ContactAuditEntry[];
  loading: boolean;
}) {
  return (
    <section className="glass-card p-5">
      <div className="mb-3 flex items-center gap-2 text-sm font-medium uppercase tracking-wide text-muted-foreground">
        <History className="h-3.5 w-3.5" /> History
      </div>
      {loading ? (
        <div className="space-y-2">
          <Skeleton className="h-10 w-full" />
          <Skeleton className="h-10 w-full" />
        </div>
      ) : audit.length === 0 ? (
        <p className="text-sm text-muted-foreground">No events logged yet.</p>
      ) : (
        <ul className="space-y-2">
          {audit.map((e) => (
            <AuditRow key={e.id} entry={e} />
          ))}
        </ul>
      )}
    </section>
  );
}

function AuditRow({ entry }: { entry: ContactAuditEntry }) {
  const summary = summarizeEvent(entry);
  return (
    <li className="flex items-start gap-3 rounded-md border border-white/5 bg-white/[0.02] px-3 py-2">
      <div className="mt-0.5 shrink-0 text-muted-foreground">
        <EventIcon eventType={entry.eventType} />
      </div>
      <div className="flex-1 space-y-0.5">
        <div className="flex items-center gap-2 text-sm">
          <span className="font-medium text-foreground">{summary.title}</span>
          <span className="text-xs text-muted-foreground">{summary.detail}</span>
        </div>
        <div className="flex flex-wrap items-center gap-3 text-[11px] text-muted-foreground">
          <span className="inline-flex items-center gap-1">
            <Clock className="h-3 w-3" />
            {new Date(entry.utc).toLocaleString()}
          </span>
          <span className="inline-flex items-center gap-1">
            <UserRound className="h-3 w-3" />
            {entry.actor}{" "}
            <span className="text-muted-foreground/60">· {entry.actorRole}</span>
          </span>
          <code className="rounded bg-white/[0.04] px-1 py-0.5 font-mono text-[10px]">
            {entry.eventType}
          </code>
        </div>
      </div>
    </li>
  );
}

function EventIcon({ eventType }: { eventType: string }) {
  if (eventType === "contact.primary.moved")
    return <ArrowRight className="h-3.5 w-3.5 text-purple-300" />;
  if (eventType.startsWith("company.contact."))
    return <Building2 className="h-3.5 w-3.5" />;
  return <UserRound className="h-3.5 w-3.5" />;
}

function summarizeEvent(entry: ContactAuditEntry): { title: string; detail: string } {
  let payload: Record<string, unknown> = {};
  try {
    payload = JSON.parse(entry.payloadJson);
  } catch {
    // Payload kept as-is — display only the canonical event-type label.
  }

  switch (entry.eventType) {
    case "contact.created":
      return {
        title: "Contact created",
        detail: payload.role
          ? `linked as ${String(payload.role)}`
          : "no company link",
      };
    case "contact.updated":
      return { title: "Contact updated", detail: "basic fields" };
    case "contact.primary.moved":
      return {
        title: "Primary moved",
        detail:
          payload.fromCompanyName && payload.toCompanyName
            ? `${String(payload.fromCompanyName)} → ${String(payload.toCompanyName)}`
            : "primary link reassigned",
      };
    case "company.contact.linked":
      return {
        title: "Linked to company",
        detail: payload.role ? `as ${String(payload.role)}` : "",
      };
    case "company.contact.unlinked":
      return { title: "Unlinked from company", detail: "" };
    case "contact.company.auto_linked":
      return {
        title: "Auto-linked via domain",
        detail: payload.domain ? `domain ${String(payload.domain)}` : "",
      };
    case "contact.deleted":
      return { title: "Contact deleted", detail: "" };
    default:
      return { title: entry.eventType, detail: "" };
  }
}

function Field({
  label,
  children,
}: {
  label: string;
  children: React.ReactNode;
}) {
  return (
    <label className="flex flex-col gap-1">
      <span className="text-xs font-medium uppercase tracking-wide text-muted-foreground">
        {label}
      </span>
      {children}
    </label>
  );
}

function DefinitionRow({
  label,
  value,
  hint,
}: {
  label: string;
  value: string | null | undefined;
  hint?: string;
}) {
  return (
    <div>
      <dt className="text-xs uppercase tracking-wide text-muted-foreground">{label}</dt>
      <dd className="mt-0.5 text-sm text-foreground">
        {value || <span className="text-muted-foreground">—</span>}
      </dd>
      {hint && <p className="mt-0.5 text-[11px] text-muted-foreground/70">{hint}</p>}
    </div>
  );
}
