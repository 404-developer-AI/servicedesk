import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import { toast } from "sonner";
import { ArrowLeft, Bell, Building2, Plus, Trash2 } from "lucide-react";
import { ApiError } from "@/lib/api";
import { authStore } from "@/auth/authStore";
import {
  companyApi,
  contactApi,
  type Company,
  type CompanyDomain,
  type CompanyInput,
  type Contact,
} from "@/lib/ticket-api";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { Switch } from "@/components/ui/switch";
import { cn } from "@/lib/utils";

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
  const [form, setForm] = useState<CompanyInput>(() => ({
    name: company.name,
    code: company.code,
    shortName: company.shortName,
    vatNumber: company.vatNumber,
    email: company.email,
    description: company.description,
    website: company.website,
    phone: company.phone,
    addressLine1: company.addressLine1,
    addressLine2: company.addressLine2,
    city: company.city,
    postalCode: company.postalCode,
    country: company.country,
    isActive: company.isActive,
    alertText: company.alertText,
    alertOnCreate: company.alertOnCreate,
    alertOnOpen: company.alertOnOpen,
    alertOnOpenMode: company.alertOnOpenMode,
  }));

  const save = useMutation({
    mutationFn: () => companyApi.update(company.id, form),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["companies"] });
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
      <div className="grid grid-cols-2 gap-3">
        <Field label="Customer code *" required>
          <Input
            value={form.code}
            onChange={(e) => setForm((f) => ({ ...f, code: e.target.value }))}
          />
        </Field>
        <Field label="Official name *" required>
          <Input
            value={form.name}
            onChange={(e) => setForm((f) => ({ ...f, name: e.target.value }))}
          />
        </Field>
        <Field label="Short name">
          <Input
            value={form.shortName ?? ""}
            onChange={(e) => setForm((f) => ({ ...f, shortName: e.target.value }))}
          />
        </Field>
        <Field label="VAT number">
          <Input
            value={form.vatNumber ?? ""}
            onChange={(e) => setForm((f) => ({ ...f, vatNumber: e.target.value }))}
          />
        </Field>
        <Field label="Website">
          <Input
            value={form.website ?? ""}
            onChange={(e) => setForm((f) => ({ ...f, website: e.target.value }))}
          />
        </Field>
        <Field label="Phone">
          <Input
            value={form.phone ?? ""}
            onChange={(e) => setForm((f) => ({ ...f, phone: e.target.value }))}
          />
        </Field>
        <Field label="Email">
          <Input
            type="email"
            value={form.email ?? ""}
            onChange={(e) => setForm((f) => ({ ...f, email: e.target.value }))}
            placeholder="info@acme.be"
          />
        </Field>
      </div>

      <div className="rounded-md border border-amber-400/20 bg-amber-400/[0.03] p-4">
        <div className="mb-3 flex items-center gap-2 text-xs font-medium uppercase tracking-wide text-amber-300">
          <Bell className="h-3.5 w-3.5" /> Alert / note
        </div>
        <Field label="Alert text">
          <textarea
            value={form.alertText ?? ""}
            onChange={(e) => setForm((f) => ({ ...f, alertText: e.target.value }))}
            rows={3}
            className="w-full rounded-md border border-white/10 bg-white/[0.04] px-3 py-2 text-sm outline-none focus:border-white/20"
          />
        </Field>
        <div className="mt-3 space-y-2">
          <label className="flex items-center justify-between gap-3 text-sm">
            <span>Pop-up when a ticket is created</span>
            <Switch
              checked={!!form.alertOnCreate}
              onCheckedChange={(v) => setForm((f) => ({ ...f, alertOnCreate: v }))}
            />
          </label>
          <label className="flex items-center justify-between gap-3 text-sm">
            <span>Pop-up when a ticket is opened</span>
            <Switch
              checked={!!form.alertOnOpen}
              onCheckedChange={(v) => setForm((f) => ({ ...f, alertOnOpen: v }))}
            />
          </label>
          {form.alertOnOpen && (
            <div className="mt-2 flex items-center gap-3 text-sm">
              <span className="text-muted-foreground">Frequency:</span>
              <label className="flex items-center gap-2">
                <input
                  type="radio"
                  name="alertMode"
                  checked={form.alertOnOpenMode === "session"}
                  onChange={() =>
                    setForm((f) => ({ ...f, alertOnOpenMode: "session" }))
                  }
                />
                Once per session
              </label>
              <label className="flex items-center gap-2">
                <input
                  type="radio"
                  name="alertMode"
                  checked={form.alertOnOpenMode === "every"}
                  onChange={() =>
                    setForm((f) => ({ ...f, alertOnOpenMode: "every" }))
                  }
                />
                Every time
              </label>
            </div>
          )}
        </div>
      </div>

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

  const { data: contacts, isLoading } = useQuery({
    queryKey: ["companies", "contacts", companyId],
    queryFn: () => companyApi.listContacts(companyId),
  });

  const candidates = useQuery({
    queryKey: ["companies", "contacts-candidates", linkQuery],
    queryFn: () => contactApi.list(linkQuery),
    enabled: linking && linkQuery.length > 1,
  });

  const unlink = useMutation({
    mutationFn: (contactId: string) => companyApi.unlinkContact(companyId, contactId),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["companies", "contacts", companyId] });
      toast.success("Contact unlinked");
    },
    onError: () => toast.error("Unlink failed"),
  });

  const link = useMutation({
    mutationFn: (contactId: string) => companyApi.linkContact(companyId, contactId),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["companies", "contacts", companyId] });
      setLinking(false);
      setLinkQuery("");
      toast.success("Contact linked");
    },
    onError: () => toast.error("Link failed"),
  });

  return (
    <section className="glass-card space-y-3 p-5">
      <div className="flex items-center justify-between">
        <h2 className="text-sm font-medium text-foreground">Linked contacts</h2>
        <Button size="sm" variant="ghost" onClick={() => setLinking((v) => !v)}>
          <Plus className="mr-1 h-3.5 w-3.5" />
          Link contact
        </Button>
      </div>

      {linking && (
        <div className="rounded-md border border-white/10 bg-white/[0.02] p-3">
          <Input
            value={linkQuery}
            onChange={(e) => setLinkQuery(e.target.value)}
            placeholder="Search contacts by email or name…"
          />
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
                    {c.companyId && c.companyId !== companyId && (
                      <Badge className="ml-2 border border-amber-400/20 bg-amber-400/10 text-[10px] font-normal text-amber-200">
                        other company
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
          {contacts.map((c) => (
            <li key={c.id} className="flex items-center justify-between py-2 text-sm">
              <div>
                <div className="text-foreground">
                  {c.firstName} {c.lastName}
                </div>
                <div className="text-xs text-muted-foreground">{c.email}</div>
              </div>
              <Button
                size="sm"
                variant="ghost"
                onClick={() => {
                  if (confirm(`Unlink ${c.email} from this company?`)) unlink.mutate(c.id);
                }}
                disabled={unlink.isPending}
              >
                <Trash2 className="h-3.5 w-3.5" />
              </Button>
            </li>
          ))}
        </ul>
      ) : (
        <p className="py-4 text-center text-sm text-muted-foreground">
          No linked contacts yet.
        </p>
      )}
    </section>
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

function Field({
  label,
  required,
  children,
}: {
  label: string;
  required?: boolean;
  children: React.ReactNode;
}) {
  return (
    <label className="flex flex-col gap-1">
      <span className="text-xs font-medium uppercase tracking-wide text-muted-foreground">
        {label}
        {required && <span className="ml-0.5 text-destructive">*</span>}
      </span>
      {children}
    </label>
  );
}
