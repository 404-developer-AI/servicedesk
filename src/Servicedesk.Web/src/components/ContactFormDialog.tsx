import * as React from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Building2, Search, X } from "lucide-react";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { cn } from "@/lib/utils";
import { ApiError } from "@/lib/api";
import { companyApi, contactApi } from "@/lib/ticket-api";
import type {
  CompanyPickerItem,
  Contact,
  ContactCompanyRole,
  ContactInput,
} from "@/lib/ticket-api";

type Props = {
  open: boolean;
  mode: "create" | "edit";
  onClose: () => void;
  onSaved: (contact: Contact) => void;
  /// When set on a create-dialog, also inserts a link to this company in
  /// the same server transaction. Shows the role picker so the caller can
  /// choose primary/secondary/supplier — defaults to 'primary'.
  forCompanyId?: string;
  /// The contact to edit (mode='edit') or prefill defaults (mode='create').
  initial?: Contact | null;
};

const ROLE_TILES: {
  role: ContactCompanyRole;
  label: string;
  hint: string;
  className: string;
  activeClassName: string;
}[] = [
  {
    role: "primary",
    label: "Primary",
    hint: "Main contact for this company",
    className: "border-purple-400/20 bg-purple-500/[0.04] text-purple-200/80",
    activeClassName: "border-purple-400/60 bg-purple-500/20 text-purple-100",
  },
  {
    role: "secondary",
    label: "Secondary",
    hint: "Also a member of this company",
    className: "border-sky-400/20 text-sky-200/70",
    activeClassName: "border-sky-400/50 bg-sky-500/15 text-sky-200",
  },
  {
    role: "supplier",
    label: "Supplier",
    hint: "External vendor / partner",
    className: "border-amber-400/20 text-amber-200/70",
    activeClassName: "border-amber-400/50 bg-amber-500/15 text-amber-200",
  },
];

export function ContactFormDialog({
  open,
  mode,
  onClose,
  onSaved,
  forCompanyId,
  initial,
}: Props) {
  const qc = useQueryClient();
  const [email, setEmail] = React.useState("");
  const [firstName, setFirstName] = React.useState("");
  const [lastName, setLastName] = React.useState("");
  const [phone, setPhone] = React.useState("");
  const [jobTitle, setJobTitle] = React.useState("");
  const [role, setRole] = React.useState<ContactCompanyRole>("primary");
  const [pickedCompany, setPickedCompany] =
    React.useState<CompanyPickerItem | null>(null);
  const [error, setError] = React.useState<string | null>(null);

  // When the caller does NOT pre-fill a company (settings → new contact,
  // new-ticket-drawer contact-picker → new contact), let the user link one
  // right away instead of forcing the detail-page → "Link company" detour.
  const showCompanyPicker = mode === "create" && !forCompanyId;

  // Effective link target: prefer the pre-filled `forCompanyId`, fall back
  // to the in-dialog picker selection. Either one turns the role tiles on.
  const effectiveCompanyId = forCompanyId ?? pickedCompany?.id ?? null;

  // Reset each time the dialog opens so stale values from a previous use
  // don't leak into the current one.
  React.useEffect(() => {
    if (!open) return;
    setEmail(initial?.email ?? "");
    setFirstName(initial?.firstName ?? "");
    setLastName(initial?.lastName ?? "");
    setPhone(initial?.phone ?? "");
    setJobTitle(initial?.jobTitle ?? "");
    setRole("primary");
    setPickedCompany(null);
    setError(null);
  }, [open, initial]);

  const save = useMutation({
    mutationFn: async () => {
      const input: ContactInput = {
        email: email.trim().toLowerCase(),
        firstName: firstName.trim(),
        lastName: lastName.trim(),
        phone: phone.trim(),
        jobTitle: jobTitle.trim(),
        ...(mode === "create" && effectiveCompanyId
          ? { companyId: effectiveCompanyId, role }
          : {}),
      };
      if (mode === "create") return contactApi.create(input);
      if (!initial) throw new Error("Edit mode requires an initial contact.");
      return contactApi.update(initial.id, input);
    },
    onSuccess: (saved) => {
      // `contacts` is the picker/typeahead cache; `companies.contacts` is the
      // company-detail's Contacts tab; `contact-companies` drives the new
      // contact-detail page's link list.
      qc.invalidateQueries({ queryKey: ["contacts"] });
      qc.invalidateQueries({ queryKey: ["companies", "contacts"] });
      qc.invalidateQueries({ queryKey: ["contact-companies", saved.id] });
      toast.success(mode === "create" ? "Contact created" : "Contact saved");
      onSaved(saved);
      onClose();
    },
    onError: (err) => {
      if (err instanceof ApiError && err.status === 400) {
        setError("Please check the fields and try again.");
      } else if (err instanceof ApiError && err.status === 409) {
        setError("A contact with this email already exists.");
      } else {
        setError("Could not save contact.");
      }
    },
  });

  const emailLooksValid = /.+@.+\..+/.test(email.trim());
  const canSubmit = emailLooksValid && !save.isPending;

  return (
    <Dialog open={open} onOpenChange={(v) => (!v ? onClose() : null)}>
      <DialogContent className="max-w-md">
        <DialogHeader>
          <DialogTitle>
            {mode === "create" ? "New contact" : "Edit contact"}
          </DialogTitle>
          <DialogDescription>
            {mode === "create" && forCompanyId
              ? "Creates the contact and links it to this company in one step."
              : mode === "create"
                ? "Creates a new contact. Link a company now, or skip and add links later."
                : "Update the contact's details."}
          </DialogDescription>
        </DialogHeader>

        <div className="space-y-3">
          <div className="grid grid-cols-2 gap-3">
            <Labelled label="First name">
              <Input
                value={firstName}
                onChange={(e) => setFirstName(e.target.value)}
                placeholder="Jan"
                autoFocus
              />
            </Labelled>
            <Labelled label="Last name">
              <Input
                value={lastName}
                onChange={(e) => setLastName(e.target.value)}
                placeholder="Janssen"
              />
            </Labelled>
          </div>
          <Labelled label="Email *" required>
            <Input
              type="email"
              value={email}
              onChange={(e) => setEmail(e.target.value)}
              placeholder="jan@acme.com"
            />
          </Labelled>
          <div className="grid grid-cols-2 gap-3">
            <Labelled label="Phone">
              <Input
                value={phone}
                onChange={(e) => setPhone(e.target.value)}
                placeholder="+32 …"
              />
            </Labelled>
            <Labelled label="Job title">
              <Input
                value={jobTitle}
                onChange={(e) => setJobTitle(e.target.value)}
                placeholder="Office manager"
              />
            </Labelled>
          </div>

          {showCompanyPicker && (
            <CompanyLinkPicker
              picked={pickedCompany}
              onPick={setPickedCompany}
              onClear={() => setPickedCompany(null)}
            />
          )}

          {mode === "create" && effectiveCompanyId && (
            <div>
              <div className="text-xs font-medium uppercase tracking-wide text-muted-foreground mb-1.5">
                Role for this company
              </div>
              <div className="grid grid-cols-3 gap-2">
                {ROLE_TILES.map((t) => {
                  const active = role === t.role;
                  return (
                    <button
                      key={t.role}
                      type="button"
                      onClick={() => setRole(t.role)}
                      className={cn(
                        "rounded-md border px-2.5 py-1.5 text-left transition-colors",
                        active ? t.activeClassName : t.className,
                        !active && "hover:bg-white/[0.04]",
                      )}
                    >
                      <div className="text-xs font-medium">{t.label}</div>
                      <div className="text-[10px] opacity-70">{t.hint}</div>
                    </button>
                  );
                })}
              </div>
            </div>
          )}

          {error && (
            <div className="rounded-md border border-destructive/30 bg-destructive/10 px-2.5 py-1.5 text-xs text-destructive-foreground">
              {error}
            </div>
          )}
        </div>

        <DialogFooter>
          <Button variant="ghost" onClick={onClose} disabled={save.isPending}>
            Cancel
          </Button>
          <Button
            onClick={() => save.mutate()}
            disabled={!canSubmit}
          >
            {save.isPending
              ? "Saving…"
              : mode === "create"
                ? "Create contact"
                : "Save changes"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

function Labelled({
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

/// Typeahead-picker for linking the new contact to a company in the same
/// server transaction. Uses `/api/companies/picker` — agent-scoped,
/// active-only, capped at 20 — so the control works for both roles. When
/// a company is picked the dialog swaps the search box for a compact chip
/// + clear button; the parent then renders the role-tile chooser.
function CompanyLinkPicker({
  picked,
  onPick,
  onClear,
}: {
  picked: CompanyPickerItem | null;
  onPick: (c: CompanyPickerItem) => void;
  onClear: () => void;
}) {
  const [search, setSearch] = React.useState("");
  const [debounced, setDebounced] = React.useState("");

  React.useEffect(() => {
    const t = setTimeout(() => setDebounced(search), 200);
    return () => clearTimeout(t);
  }, [search]);

  const { data: matches } = useQuery({
    queryKey: ["companies", "picker", debounced],
    queryFn: () => companyApi.picker(debounced || undefined),
    // Only fetch once the user actually starts typing OR opens the picker
    // empty (the backend returns the first 20 active companies on an empty
    // query, which doubles as a discovery list).
    enabled: picked === null,
    placeholderData: (prev) => prev,
  });

  if (picked) {
    return (
      <div>
        <div className="text-xs font-medium uppercase tracking-wide text-muted-foreground mb-1.5">
          Link company
        </div>
        <div className="flex items-center justify-between gap-2 rounded-md border border-white/10 bg-white/[0.04] px-3 py-2 text-sm">
          <span className="flex min-w-0 items-center gap-2">
            <Building2 className="h-3.5 w-3.5 shrink-0 text-muted-foreground" />
            <span className="truncate">{picked.shortName || picked.name}</span>
            <span className="font-mono text-[10px] text-muted-foreground">
              {picked.code}
            </span>
          </span>
          <button
            type="button"
            onClick={onClear}
            className="shrink-0 rounded-md p-1 text-muted-foreground/70 hover:bg-white/[0.06] hover:text-foreground"
            title="Unpick company"
          >
            <X className="h-3.5 w-3.5" />
          </button>
        </div>
      </div>
    );
  }

  return (
    <div>
      <div className="text-xs font-medium uppercase tracking-wide text-muted-foreground mb-1.5">
        Link company <span className="normal-case text-muted-foreground/60">(optional)</span>
      </div>
      <div className="flex items-center gap-2 rounded-md border border-white/10 bg-white/[0.04] px-2.5">
        <Search className="h-3.5 w-3.5 shrink-0 text-muted-foreground" />
        <input
          value={search}
          onChange={(e) => setSearch(e.target.value)}
          placeholder="Search active companies…"
          className="h-9 flex-1 bg-transparent text-sm outline-none placeholder:text-muted-foreground"
        />
      </div>
      {(matches ?? []).length > 0 && (
        <ul className="mt-1.5 max-h-40 space-y-0.5 overflow-auto rounded-md border border-white/5 bg-white/[0.02] p-1">
          {(matches ?? []).map((c) => (
            <li key={c.id}>
              <button
                type="button"
                onClick={() => {
                  onPick(c);
                  setSearch("");
                }}
                className="flex w-full items-center gap-2 rounded-md px-2 py-1.5 text-left text-sm hover:bg-white/[0.05]"
              >
                <Building2 className="h-3.5 w-3.5 shrink-0 text-muted-foreground" />
                <span className="truncate">{c.shortName || c.name}</span>
                <span className="ml-auto font-mono text-[10px] text-muted-foreground">
                  {c.code}
                </span>
              </button>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
