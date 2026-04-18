import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import { toast } from "sonner";
import { Bell, ExternalLink } from "lucide-react";
import { ApiError } from "@/lib/api";
import { companyApi, type Company, type CompanyInput } from "@/lib/ticket-api";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { Switch } from "@/components/ui/switch";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";

type EditingState = Company | null | "new";

export function CompaniesSettingsPage() {
  const [search, setSearch] = useState("");
  const [includeInactive, setIncludeInactive] = useState(false);
  const [editing, setEditing] = useState<EditingState>(null);

  const { data, isLoading } = useQuery({
    queryKey: ["companies", "list", search, includeInactive],
    queryFn: () => companyApi.list(search || undefined, includeInactive),
  });

  return (
    <div className="flex min-h-[calc(100vh-8rem)] w-full flex-col gap-6">
      <header className="flex items-start justify-between gap-4">
        <div className="space-y-1">
          <h1 className="text-display-md font-semibold text-foreground">Companies</h1>
          <p className="text-sm text-muted-foreground">
            Manage customer companies: code, official name, short name, VAT number and
            contact details. Each company can have an alert note that automatically
            pops up when linked tickets are created or opened.
          </p>
        </div>
        <Badge className="border border-white/10 bg-white/[0.05] text-xs font-normal text-muted-foreground">
          Admin only
        </Badge>
      </header>

      <div className="flex flex-wrap items-center justify-between gap-3">
        <div className="flex flex-1 items-center gap-3">
          <Input
            value={search}
            onChange={(e) => setSearch(e.target.value)}
            placeholder="Search by name, short name, code or VAT…"
            className="max-w-md"
          />
          <label className="flex items-center gap-2 text-xs text-muted-foreground">
            <Switch checked={includeInactive} onCheckedChange={setIncludeInactive} />
            Show inactive
          </label>
        </div>
        <Button onClick={() => setEditing("new")}>+ New company</Button>
      </div>

      {isLoading ? (
        <div className="space-y-2">
          {[...Array(4)].map((_, i) => (
            <Skeleton key={i} className="h-12 w-full" />
          ))}
        </div>
      ) : (
        <section className="glass-card overflow-hidden">
          <table className="w-full text-left text-sm">
            <thead className="text-xs uppercase tracking-wide text-muted-foreground [&_th]:border-b [&_th]:border-white/10">
              <tr>
                <th className="px-4 py-3 font-medium">Code</th>
                <th className="px-4 py-3 font-medium">Name</th>
                <th className="px-4 py-3 font-medium">Short name</th>
                <th className="px-4 py-3 font-medium">VAT</th>
                <th className="px-4 py-3 font-medium">Alert</th>
                <th className="px-4 py-3 font-medium">Status</th>
                <th className="px-4 py-3" />
              </tr>
            </thead>
            <tbody>
              {data?.map((c) => (
                <tr key={c.id} className="border-b border-white/5 hover:bg-white/[0.03]">
                  <td className="px-4 py-3 font-mono text-xs text-muted-foreground">{c.code}</td>
                  <td className="px-4 py-3 text-foreground">{c.name}</td>
                  <td className="px-4 py-3 text-muted-foreground">{c.shortName || "—"}</td>
                  <td className="px-4 py-3 font-mono text-xs text-muted-foreground">
                    {c.vatNumber || "—"}
                  </td>
                  <td className="px-4 py-3">
                    {c.alertText && (c.alertOnCreate || c.alertOnOpen) ? (
                      <span
                        className="inline-flex items-center gap-1 text-amber-300"
                        title={c.alertText}
                      >
                        <Bell className="h-3.5 w-3.5" />
                        <span className="text-[10px] uppercase tracking-wide">
                          {c.alertOnCreate && c.alertOnOpen
                            ? "create + open"
                            : c.alertOnCreate
                              ? "create"
                              : "open"}
                        </span>
                      </span>
                    ) : (
                      <span className="text-xs text-muted-foreground">—</span>
                    )}
                  </td>
                  <td className="px-4 py-3">
                    {c.isActive ? (
                      <Badge className="border border-emerald-400/20 bg-emerald-400/10 text-[10px] font-normal text-emerald-200">
                        active
                      </Badge>
                    ) : (
                      <Badge className="border border-white/10 bg-white/[0.05] text-[10px] font-normal text-muted-foreground">
                        inactive
                      </Badge>
                    )}
                  </td>
                  <td className="px-4 py-3 text-right">
                    <Link
                      to="/companies/$companyId"
                      params={{ companyId: c.id }}
                      className="mr-2 inline-flex items-center gap-1 text-xs text-muted-foreground hover:text-foreground"
                    >
                      Detail <ExternalLink className="h-3 w-3" />
                    </Link>
                    <Button variant="ghost" size="sm" onClick={() => setEditing(c)}>
                      Edit
                    </Button>
                  </td>
                </tr>
              ))}
              {data && data.length === 0 && (
                <tr>
                  <td colSpan={7} className="p-8 text-center text-sm text-muted-foreground">
                    No companies found.
                  </td>
                </tr>
              )}
            </tbody>
          </table>
        </section>
      )}

      {editing && (
        <CompanyDialog
          company={editing === "new" ? null : editing}
          onClose={() => setEditing(null)}
        />
      )}
    </div>
  );
}

function CompanyDialog({
  company,
  onClose,
}: {
  company: Company | null;
  onClose: () => void;
}) {
  const qc = useQueryClient();
  const [form, setForm] = useState<CompanyInput>(() => ({
    name: company?.name ?? "",
    code: company?.code ?? "",
    shortName: company?.shortName ?? "",
    vatNumber: company?.vatNumber ?? "",
    description: company?.description ?? "",
    website: company?.website ?? "",
    phone: company?.phone ?? "",
    addressLine1: company?.addressLine1 ?? "",
    addressLine2: company?.addressLine2 ?? "",
    city: company?.city ?? "",
    postalCode: company?.postalCode ?? "",
    country: company?.country ?? "",
    isActive: company?.isActive ?? true,
    alertText: company?.alertText ?? "",
    alertOnCreate: company?.alertOnCreate ?? false,
    alertOnOpen: company?.alertOnOpen ?? false,
    alertOnOpenMode: company?.alertOnOpenMode ?? "session",
  }));

  const save = useMutation({
    mutationFn: async () => {
      if (company) return companyApi.update(company.id, form);
      return companyApi.create(form);
    },
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["companies"] });
      toast.success(company ? "Company updated" : "Company created");
      onClose();
    },
    onError: (err) => {
      if (err instanceof ApiError) {
        if (err.status === 409) {
          toast.error("A company with this code already exists.");
          return;
        }
        if (err.status === 400) {
          toast.error("Check the fields: name and code are required.");
          return;
        }
        toast.error(`Save failed (${err.status})`);
      } else {
        toast.error("Save failed");
      }
    },
  });

  const remove = useMutation({
    mutationFn: async () => {
      if (!company) return;
      await companyApi.remove(company.id);
    },
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["companies"] });
      toast.success("Company deactivated");
      onClose();
    },
    onError: () => toast.error("Deactivation failed"),
  });

  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent className="max-w-2xl">
        <DialogHeader>
          <DialogTitle>{company ? "Edit company" : "New company"}</DialogTitle>
          <DialogDescription>
            Customer code is required and unique. Alerts appear as a pop-up on
            linked tickets.
          </DialogDescription>
        </DialogHeader>

        <div className="grid gap-4 py-2">
          <div className="grid grid-cols-2 gap-3">
            <Field label="Customer code *" required>
              <Input
                value={form.code}
                onChange={(e) => setForm((f) => ({ ...f, code: e.target.value }))}
                placeholder="ACME001"
              />
            </Field>
            <Field label="Official name *" required>
              <Input
                value={form.name}
                onChange={(e) => setForm((f) => ({ ...f, name: e.target.value }))}
                placeholder="Acme International BV"
              />
            </Field>
            <Field label="Short name">
              <Input
                value={form.shortName ?? ""}
                onChange={(e) => setForm((f) => ({ ...f, shortName: e.target.value }))}
                placeholder="Acme"
              />
            </Field>
            <Field label="VAT number">
              <Input
                value={form.vatNumber ?? ""}
                onChange={(e) => setForm((f) => ({ ...f, vatNumber: e.target.value }))}
                placeholder="BE0123456789"
              />
            </Field>
            <Field label="Website">
              <Input
                value={form.website ?? ""}
                onChange={(e) => setForm((f) => ({ ...f, website: e.target.value }))}
                placeholder="https://…"
              />
            </Field>
            <Field label="Phone">
              <Input
                value={form.phone ?? ""}
                onChange={(e) => setForm((f) => ({ ...f, phone: e.target.value }))}
              />
            </Field>
          </div>

          <details className="group rounded-md border border-white/10 bg-white/[0.02] px-3 py-2">
            <summary className="cursor-pointer text-xs font-medium uppercase tracking-wide text-muted-foreground">
              Address (optional)
            </summary>
            <div className="mt-3 grid grid-cols-2 gap-3">
              <Field label="Address line 1"><Input
                value={form.addressLine1 ?? ""}
                onChange={(e) => setForm((f) => ({ ...f, addressLine1: e.target.value }))}
              /></Field>
              <Field label="Address line 2"><Input
                value={form.addressLine2 ?? ""}
                onChange={(e) => setForm((f) => ({ ...f, addressLine2: e.target.value }))}
              /></Field>
              <Field label="Postal code"><Input
                value={form.postalCode ?? ""}
                onChange={(e) => setForm((f) => ({ ...f, postalCode: e.target.value }))}
              /></Field>
              <Field label="City"><Input
                value={form.city ?? ""}
                onChange={(e) => setForm((f) => ({ ...f, city: e.target.value }))}
              /></Field>
              <Field label="Country"><Input
                value={form.country ?? ""}
                onChange={(e) => setForm((f) => ({ ...f, country: e.target.value }))}
              /></Field>
            </div>
          </details>

          <div className="rounded-md border border-amber-400/20 bg-amber-400/[0.03] px-3 py-3">
            <div className="mb-2 flex items-center gap-2 text-xs font-medium uppercase tracking-wide text-amber-300">
              <Bell className="h-3.5 w-3.5" /> Alert / note
            </div>
            <Field label="Alert text (shown in pop-up)">
              <textarea
                value={form.alertText ?? ""}
                onChange={(e) => setForm((f) => ({ ...f, alertText: e.target.value }))}
                rows={3}
                className="w-full rounded-md border border-white/10 bg-white/[0.04] px-3 py-2 text-sm outline-none focus:border-white/20"
                placeholder="E.g. VIP customer — always call back by phone."
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
                <div className="rounded-md bg-white/[0.03] p-3">
                  <div className="mb-2 text-xs font-medium uppercase tracking-wide text-muted-foreground">
                    How often on open?
                  </div>
                  <div className="grid grid-cols-2 gap-2">
                    <ModeOption
                      active={form.alertOnOpenMode === "session"}
                      onClick={() => setForm((f) => ({ ...f, alertOnOpenMode: "session" }))}
                      title="Once per session"
                      description="Shows the first time you open this ticket in your browser session."
                    />
                    <ModeOption
                      active={form.alertOnOpenMode === "every"}
                      onClick={() => setForm((f) => ({ ...f, alertOnOpenMode: "every" }))}
                      title="Every time"
                      description="Shows on every navigation to or refresh of the ticket."
                    />
                  </div>
                </div>
              )}
            </div>
          </div>

          <label className="flex items-center gap-2 text-sm">
            <Switch
              checked={!!form.isActive}
              onCheckedChange={(v) => setForm((f) => ({ ...f, isActive: v }))}
            />
            Active
          </label>
        </div>

        <DialogFooter className="gap-2 sm:justify-between">
          {company && (
            <Button
              variant="ghost"
              onClick={() => {
                if (confirm(`Deactivate company "${company.name}"?`)) remove.mutate();
              }}
              disabled={remove.isPending}
              className="text-destructive hover:text-destructive"
            >
              Deactivate
            </Button>
          )}
          <div className="flex gap-2">
            <Button variant="ghost" onClick={onClose}>Cancel</Button>
            <Button onClick={() => save.mutate()} disabled={save.isPending}>
              {save.isPending ? "Saving…" : "Save"}
            </Button>
          </div>
        </DialogFooter>
      </DialogContent>
    </Dialog>
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

function ModeOption({
  active,
  onClick,
  title,
  description,
}: {
  active: boolean;
  onClick: () => void;
  title: string;
  description: string;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      className={`rounded-md border p-3 text-left transition-colors ${
        active
          ? "border-amber-400/40 bg-amber-400/[0.06]"
          : "border-white/10 bg-white/[0.02] hover:bg-white/[0.04]"
      }`}
    >
      <div className="text-sm font-medium text-foreground">{title}</div>
      <div className="mt-1 text-xs text-muted-foreground">{description}</div>
    </button>
  );
}
