import { useMemo, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import { toast } from "sonner";
import { ArrowLeft, Bell, Building2, Pencil, Plus, Trash2, UserPlus } from "lucide-react";
import { ApiError } from "@/lib/api";
import { authStore } from "@/auth/authStore";
import {
  companyApi,
  contactApi,
  type Company,
  type CompanyDomain,
  type CompanyInput,
  type Contact,
  type ContactCompanyRole,
} from "@/lib/ticket-api";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import { Skeleton } from "@/components/ui/skeleton";
import { ContactFormDialog } from "@/components/ContactFormDialog";
import { CompanyFormFields, companyToInput } from "@/components/CompanyFormFields";
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

type TabKey = "overview" | "contacts" | "domains";

export function CompanyDetailPage({ companyId }: { companyId: string }) {
  const role = authStore.get().user?.role ?? null;
  const isAdmin = role === "Admin";
  const [tab, setTab] = useState<TabKey>("overview");

  const { data, isLoading } = useQuery({
    queryKey: ["companies", "detail", companyId],
    queryFn: () => companyApi.get(companyId),
  });

  if (isLoading) return <DetailSkeleton />;
  if (!data) {
    return (
      <div className="p-8 text-sm text-muted-foreground">
        Company not found.
      </div>
    );
  }

  const { company, domains } = data;

  const tabs: { key: TabKey; label: string; visible: boolean }[] = [
    { key: "overview", label: "Overview", visible: true },
    { key: "contacts", label: "Contacts", visible: true },
    { key: "domains", label: "Domains", visible: isAdmin },
  ];

  return (
    <div className="mx-auto flex w-full max-w-4xl flex-col gap-6 p-6">
      <Link
        to="/tickets"
        className="inline-flex items-center gap-1 text-xs text-muted-foreground hover:text-foreground"
      >
        <ArrowLeft className="h-3 w-3" /> Back
      </Link>

      <header className="flex items-start justify-between gap-4">
        <div className="flex items-start gap-3">
          <div className="rounded-lg bg-white/[0.05] p-2.5">
            <Building2 className="h-5 w-5 text-foreground" />
          </div>
          <div>
            <h1 className="text-display-md font-semibold text-foreground">
              {company.name}
            </h1>
            <div className="mt-1 flex items-center gap-2 text-xs text-muted-foreground">
              <span className="font-mono">{company.code}</span>
              {company.shortName && (
                <>
                  <span>·</span>
                  <span>{company.shortName}</span>
                </>
              )}
              {company.vatNumber && (
                <>
                  <span>·</span>
                  <span className="font-mono">{company.vatNumber}</span>
                </>
              )}
              {!company.isActive && (
                <Badge className="border border-white/10 bg-white/[0.05] text-[10px] font-normal text-muted-foreground">
                  inactive
                </Badge>
              )}
            </div>
          </div>
        </div>
        {company.alertText && (company.alertOnCreate || company.alertOnOpen) && (
          <div className="flex max-w-sm items-start gap-2 rounded-md border border-amber-400/30 bg-amber-400/[0.05] px-3 py-2 text-xs text-amber-200">
            <Bell className="mt-0.5 h-3.5 w-3.5 shrink-0" />
            <span className="whitespace-pre-wrap">{company.alertText}</span>
          </div>
        )}
      </header>

      <nav className="glass-card flex flex-wrap gap-1 p-1">
        {tabs
          .filter((t) => t.visible)
          .map((t) => (
            <button
              key={t.key}
              type="button"
              onClick={() => setTab(t.key)}
              className={cn(
                "flex-1 rounded-md px-4 py-2 text-sm font-medium transition-colors",
                tab === t.key
                  ? "bg-white/[0.08] text-foreground shadow-[inset_0_0_0_1px_rgba(255,255,255,0.08)]"
                  : "text-muted-foreground hover:bg-white/[0.04] hover:text-foreground",
              )}
            >
              {t.label}
            </button>
          ))}
      </nav>

      {tab === "overview" && <OverviewTab company={company} />}
      {tab === "contacts" && <ContactsTab companyId={companyId} />}
      {tab === "domains" && isAdmin && (
        <DomainsTab companyId={companyId} domains={domains} />
      )}
    </div>
  );
}

function DetailSkeleton() {
  return (
    <div className="mx-auto w-full max-w-4xl space-y-4 p-6">
      <Skeleton className="h-8 w-48" />
      <Skeleton className="h-32 w-full" />
      <Skeleton className="h-64 w-full" />
    </div>
  );
}

// ---- Overview tab (editable) ----
function OverviewTab({ company }: { company: Company }) {
  const qc = useQueryClient();
  const [form, setForm] = useState<CompanyInput>(() => companyToInput(company));

  const save = useMutation({
    mutationFn: () => companyApi.update(company.id, form),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["companies"] });
      qc.invalidateQueries({ queryKey: ["company", company.id] });
      toast.success("Changes saved");
    },
    onError: (err) => {
      if (err instanceof ApiError && err.status === 409) {
        toast.error("Another company already uses this code.");
      } else if (err instanceof ApiError && err.status === 403) {
        toast.error("You don't have permission to edit this company.");
      } else {
        toast.error("Save failed");
      }
    },
  });

  return (
    <section className="glass-card space-y-5 p-5">
      <CompanyFormFields form={form} setForm={setForm} />
      <div className="flex justify-end">
        <Button onClick={() => save.mutate()} disabled={save.isPending}>
          {save.isPending ? "Saving…" : "Save changes"}
        </Button>
      </div>
    </section>
  );
}

// ---- Contacts tab ----
function ContactsTab({ companyId }: { companyId: string }) {
  const qc = useQueryClient();
  const [linking, setLinking] = useState(false);
  const [linkQuery, setLinkQuery] = useState("");
  const [creating, setCreating] = useState(false);
  const [editing, setEditing] = useState<Contact | null>(null);
  const [promoteCandidate, setPromoteCandidate] = useState<Contact | null>(null);

  const { data: contacts, isLoading } = useQuery({
    queryKey: ["companies", "contacts", companyId],
    queryFn: () => companyApi.listContacts(companyId),
  });

  const { data: links } = useQuery({
    queryKey: ["companies", "links", companyId],
    queryFn: () => companyApi.links(companyId),
  });

  // Map contact-id → role for this company so each row can render a correct
  // starting value in its role select. A contact may (rarely) not have a
  // matching link yet if the API returns eventually-consistent pages — we
  // default to 'secondary' in that case.
  const roleByContact = useMemo(() => {
    const m = new Map<string, ContactCompanyRole>();
    for (const l of links ?? []) m.set(l.contactId, l.role);
    return m;
  }, [links]);

  const candidates = useQuery({
    queryKey: ["companies", "contacts-candidates", linkQuery],
    queryFn: () => contactApi.list(linkQuery),
    enabled: linking && linkQuery.length > 1,
  });

  const invalidate = () => {
    qc.invalidateQueries({ queryKey: ["companies", "contacts", companyId] });
    qc.invalidateQueries({ queryKey: ["companies", "links", companyId] });
  };

  const unlink = useMutation({
    mutationFn: (contactId: string) => companyApi.unlinkContact(companyId, contactId),
    onSuccess: () => {
      invalidate();
      toast.success("Contact unlinked");
    },
    onError: () => toast.error("Unlink failed"),
  });

  const link = useMutation({
    mutationFn: (contactId: string) =>
      companyApi.linkContact(companyId, contactId, "secondary"),
    onSuccess: () => {
      invalidate();
      setLinking(false);
      setLinkQuery("");
      toast.success("Contact linked as secondary");
    },
    onError: () => toast.error("Link failed"),
  });

  const setRole = useMutation({
    mutationFn: ({
      contactId,
      role,
    }: {
      contactId: string;
      role: ContactCompanyRole;
    }) => companyApi.linkContact(companyId, contactId, role),
    onSuccess: (_, vars) => {
      invalidate();
      qc.invalidateQueries({ queryKey: ["contact-companies", vars.contactId] });
      toast.success("Role updated");
    },
    onError: () => toast.error("Role change failed"),
  });

  const handleRoleChange = (contact: Contact, nextRole: ContactCompanyRole) => {
    const currentRole = roleByContact.get(contact.id);
    if (currentRole === nextRole) return;
    // Promoting to primary while the contact is already primary elsewhere
    // demotes that other link server-side. Surface this so agents can't
    // "steal" a primary silently — explicit confirmation, then commit.
    const hasPrimaryElsewhere =
      nextRole === "primary" &&
      contact.primaryCompanyId &&
      contact.primaryCompanyId !== companyId;
    if (hasPrimaryElsewhere) {
      setPromoteCandidate(contact);
      return;
    }
    setRole.mutate({ contactId: contact.id, role: nextRole });
  };

  return (
    <section className="glass-card space-y-3 p-5">
      <div className="flex items-center justify-between">
        <h2 className="text-sm font-medium text-foreground">Linked contacts</h2>
        <div className="flex gap-1">
          <Button size="sm" variant="ghost" onClick={() => setLinking((v) => !v)}>
            <Plus className="mr-1 h-3.5 w-3.5" />
            Link contact
          </Button>
          <Button size="sm" variant="ghost" onClick={() => setCreating(true)}>
            <UserPlus className="mr-1 h-3.5 w-3.5" />
            New contact
          </Button>
        </div>
      </div>

      {linking && (
        <div className="rounded-md border border-white/10 bg-white/[0.02] p-3">
          <Input
            value={linkQuery}
            onChange={(e) => setLinkQuery(e.target.value)}
            placeholder="Search contacts by email or name…"
          />
          <p className="mt-1 text-[10px] text-muted-foreground">
            Linked contacts start as <span className="text-sky-300">secondary</span>. Change the role inline after.
          </p>
          {candidates.data && candidates.data.length > 0 && (
            <ul className="mt-2 max-h-48 space-y-1 overflow-y-auto">
              {candidates.data.map((c: Contact) => (
                <li
                  key={c.id}
                  className="flex items-center justify-between rounded-md px-2 py-1 text-sm hover:bg-white/[0.04]"
                >
                  <span>
                    {c.firstName} {c.lastName}{" "}
                    <span className="text-xs text-muted-foreground">{c.email}</span>
                    {c.primaryCompanyId && c.primaryCompanyId !== companyId && (
                      <Badge className="ml-2 border border-amber-400/20 bg-amber-400/10 text-[10px] font-normal text-amber-200">
                        primary elsewhere
                      </Badge>
                    )}
                  </span>
                  <Button
                    size="sm"
                    variant="ghost"
                    onClick={() => link.mutate(c.id)}
                    disabled={link.isPending}
                  >
                    Link
                  </Button>
                </li>
              ))}
            </ul>
          )}
        </div>
      )}

      {isLoading ? (
        <Skeleton className="h-24 w-full" />
      ) : contacts && contacts.length > 0 ? (
        <ul className="divide-y divide-white/5">
          {contacts.map((c) => {
            const role = roleByContact.get(c.id) ?? "secondary";
            return (
              <li
                key={c.id}
                className="flex items-center justify-between gap-3 py-2 text-sm"
              >
                <div className="min-w-0 flex-1">
                  <div className="truncate text-foreground">
                    {c.firstName} {c.lastName}
                  </div>
                  <div className="truncate text-xs text-muted-foreground">
                    {c.email}
                  </div>
                </div>
                <RoleSelect
                  value={role}
                  disabled={setRole.isPending}
                  onChange={(r) => handleRoleChange(c, r)}
                />
                <Button
                  size="sm"
                  variant="ghost"
                  title="Edit contact"
                  onClick={() => setEditing(c)}
                >
                  <Pencil className="h-3.5 w-3.5" />
                </Button>
                <Button
                  size="sm"
                  variant="ghost"
                  title="Unlink from this company"
                  onClick={() => {
                    if (confirm(`Unlink ${c.email} from this company?`)) unlink.mutate(c.id);
                  }}
                  disabled={unlink.isPending}
                >
                  <Trash2 className="h-3.5 w-3.5" />
                </Button>
              </li>
            );
          })}
        </ul>
      ) : (
        <p className="py-4 text-center text-sm text-muted-foreground">
          No linked contacts yet.
        </p>
      )}

      <ContactFormDialog
        open={creating}
        mode="create"
        forCompanyId={companyId}
        onClose={() => setCreating(false)}
        onSaved={() => invalidate()}
      />
      <ContactFormDialog
        open={!!editing}
        mode="edit"
        initial={editing}
        onClose={() => setEditing(null)}
        onSaved={() => invalidate()}
      />

      <PromotePrimaryDialog
        contact={promoteCandidate}
        onCancel={() => setPromoteCandidate(null)}
        onConfirm={() => {
          if (!promoteCandidate) return;
          setRole.mutate({ contactId: promoteCandidate.id, role: "primary" });
          setPromoteCandidate(null);
        }}
      />
    </section>
  );
}

function RoleSelect({
  value,
  onChange,
  disabled,
}: {
  value: ContactCompanyRole;
  onChange: (r: ContactCompanyRole) => void;
  disabled?: boolean;
}) {
  const badge = ROLE_BADGE[value];
  return (
    <Select
      value={value}
      onValueChange={(v) => onChange(v as ContactCompanyRole)}
      disabled={disabled}
    >
      <SelectTrigger
        className={cn(
          "h-7 w-[110px] border px-2 py-0 text-[11px] font-medium",
          badge.className,
        )}
      >
        <SelectValue />
      </SelectTrigger>
      <SelectContent>
        <SelectItem value="primary">Primary</SelectItem>
        <SelectItem value="secondary">Secondary</SelectItem>
        <SelectItem value="supplier">Supplier</SelectItem>
      </SelectContent>
    </Select>
  );
}

function PromotePrimaryDialog({
  contact,
  onCancel,
  onConfirm,
}: {
  contact: Contact | null;
  onCancel: () => void;
  onConfirm: () => void;
}) {
  return (
    <Dialog open={!!contact} onOpenChange={(v) => (!v ? onCancel() : null)}>
      <DialogContent className="max-w-md">
        <DialogHeader>
          <DialogTitle>Promote to primary here?</DialogTitle>
          <DialogDescription>
            {contact ? (
              <>
                <span className="font-medium text-foreground">
                  {contact.firstName} {contact.lastName}
                </span>{" "}
                is currently primary at another company. Making them primary
                here will demote that other link to <em>secondary</em> in one
                transaction. Historical tickets on the other company stay where
                they are.
              </>
            ) : null}
          </DialogDescription>
        </DialogHeader>
        <DialogFooter>
          <Button variant="ghost" onClick={onCancel}>
            Cancel
          </Button>
          <Button onClick={onConfirm}>Make primary here</Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

// ---- Domains tab (admin only) ----
function DomainsTab({
  companyId,
  domains,
}: {
  companyId: string;
  domains: CompanyDomain[];
}) {
  const qc = useQueryClient();
  const [value, setValue] = useState("");

  const add = useMutation({
    mutationFn: () => companyApi.addDomain(companyId, value.trim().toLowerCase()),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["companies", "detail", companyId] });
      setValue("");
      toast.success("Domain added");
    },
    onError: (err) => {
      if (err instanceof ApiError && err.status === 409) {
        toast.error("This domain is already linked.");
      } else {
        toast.error("Add failed");
      }
    },
  });

  const remove = useMutation({
    mutationFn: (domainId: string) => companyApi.removeDomain(companyId, domainId),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["companies", "detail", companyId] });
      toast.success("Domain removed");
    },
    onError: () => toast.error("Remove failed"),
  });

  return (
    <section className="glass-card space-y-3 p-5">
      <h2 className="text-sm font-medium text-foreground">Email domains</h2>
      <p className="text-xs text-muted-foreground">
        Incoming mail from these domains is automatically linked to this company
        (unless disabled in Settings → Mail).
      </p>
      <div className="flex gap-2">
        <Input
          value={value}
          onChange={(e) => setValue(e.target.value)}
          placeholder="acme.com"
          onKeyDown={(e) => {
            if (e.key === "Enter" && value.trim()) add.mutate();
          }}
        />
        <Button
          onClick={() => add.mutate()}
          disabled={!value.trim() || add.isPending}
        >
          Add
        </Button>
      </div>
      {domains.length === 0 ? (
        <p className="py-4 text-center text-sm text-muted-foreground">
          No domains yet.
        </p>
      ) : (
        <ul className="divide-y divide-white/5">
          {domains.map((d) => (
            <li key={d.id} className="flex items-center justify-between py-2 text-sm">
              <span className="font-mono text-foreground">{d.domain}</span>
              <Button
                size="sm"
                variant="ghost"
                onClick={() => {
                  if (confirm(`Remove domain "${d.domain}"?`)) remove.mutate(d.id);
                }}
                disabled={remove.isPending}
              >
                <Trash2 className="h-3.5 w-3.5" />
              </Button>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}

