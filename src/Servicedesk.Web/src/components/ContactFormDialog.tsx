import * as React from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
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
import { contactApi } from "@/lib/ticket-api";
import type { Contact, ContactCompanyRole, ContactInput } from "@/lib/ticket-api";

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
  const [error, setError] = React.useState<string | null>(null);

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
        ...(mode === "create" && forCompanyId
          ? { companyId: forCompanyId, role }
          : {}),
      };
      if (mode === "create") return contactApi.create(input);
      if (!initial) throw new Error("Edit mode requires an initial contact.");
      return contactApi.update(initial.id, input);
    },
    onSuccess: (saved) => {
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
                ? "Creates a new contact without a company link."
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

          {mode === "create" && forCompanyId && (
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
