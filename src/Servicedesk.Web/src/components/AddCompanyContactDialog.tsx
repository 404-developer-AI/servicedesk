import * as React from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { UserRound } from "lucide-react";
import {
  contactApi,
  companyApi,
  type Contact,
  type ContactCompanyRole,
} from "@/lib/ticket-api";
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
import { cn } from "@/lib/utils";

type Props = {
  companyId: string;
  existingContactIds: Set<string>;
  onClose: () => void;
};

/// Shared "+ Link contact" dialog for the company side. Inverse of
/// AddContactLinkDialog: pick an existing contact, pick a role, POST to the
/// same endpoint `POST /api/companies/{id}/contacts/{contactId}`. Used from
/// the ticket side-panel's Company tab (and available for other entry points
/// that want to attach an existing contact to a company).
export function AddCompanyContactDialog({
  companyId,
  existingContactIds,
  onClose,
}: Props) {
  const qc = useQueryClient();
  const [search, setSearch] = React.useState("");
  const [debouncedSearch, setDebouncedSearch] = React.useState("");
  const [target, setTarget] = React.useState<Contact | null>(null);
  const [role, setRole] = React.useState<ContactCompanyRole>("secondary");

  React.useEffect(() => {
    const timer = setTimeout(() => setDebouncedSearch(search), 200);
    return () => clearTimeout(timer);
  }, [search]);

  const { data: matches } = useQuery({
    queryKey: ["contacts", "picker", debouncedSearch],
    queryFn: () => contactApi.list(debouncedSearch || undefined),
    placeholderData: (prev) => prev,
  });

  const addLink = useMutation({
    mutationFn: async () => {
      if (!target) throw new Error("Pick a contact first.");
      await companyApi.linkContact(companyId, target.id, role);
    },
    onSuccess: () => {
      // Company-side queries (lists, links) and contact-side caches both need
      // to refresh — the contact's `contact-companies` set just grew.
      qc.invalidateQueries({ queryKey: ["companies", "contacts", companyId] });
      qc.invalidateQueries({ queryKey: ["companies", "links", companyId] });
      qc.invalidateQueries({ queryKey: ["contact-companies", target?.id] });
      qc.invalidateQueries({ queryKey: ["contact-audit", target?.id] });
      qc.invalidateQueries({ queryKey: ["contacts"] });
      toast.success(
        `${target?.firstName || target?.email} linked to this company`,
      );
      onClose();
    },
    onError: () => toast.error("Could not link contact"),
  });

  const targetAlreadyLinked = target ? existingContactIds.has(target.id) : false;

  return (
    <Dialog open onOpenChange={(v) => (!v ? onClose() : null)}>
      <DialogContent className="max-w-lg">
        <DialogHeader>
          <DialogTitle>Link contact</DialogTitle>
          <DialogDescription>
            Search an existing contact and pick the role they play in this company. You
            can link the same contact to multiple companies.
          </DialogDescription>
        </DialogHeader>

        <div className="space-y-3">
          <div>
            <div className="mb-1 text-[10px] uppercase tracking-wide text-muted-foreground">
              Contact
            </div>
            <Input
              autoFocus
              placeholder="Search by name or email…"
              value={search}
              onChange={(e) => setSearch(e.target.value)}
            />
            <div className="mt-2 max-h-[220px] space-y-1 overflow-auto rounded-md border border-white/5 bg-white/[0.02] p-1">
              {(matches ?? []).length === 0 ? (
                <div className="px-3 py-3 text-xs text-muted-foreground">No matches.</div>
              ) : (
                (matches ?? []).map((c) => {
                  const alreadyLinked = existingContactIds.has(c.id);
                  const selected = target?.id === c.id;
                  const fullName =
                    [c.firstName, c.lastName].filter(Boolean).join(" ").trim() || c.email;
                  return (
                    <button
                      key={c.id}
                      type="button"
                      onClick={() => setTarget(c)}
                      disabled={alreadyLinked}
                      className={cn(
                        "flex w-full items-center justify-between gap-2 rounded-md px-2 py-1.5 text-left text-sm",
                        selected
                          ? "border border-purple-400/50 bg-purple-500/10"
                          : "hover:bg-white/[0.05]",
                        alreadyLinked && "cursor-not-allowed opacity-40",
                      )}
                    >
                      <span className="flex min-w-0 items-center gap-2">
                        <UserRound className="h-3.5 w-3.5 text-muted-foreground shrink-0" />
                        <span className="truncate">{fullName}</span>
                        {fullName !== c.email && (
                          <span className="truncate text-[10px] text-muted-foreground">
                            {c.email}
                          </span>
                        )}
                      </span>
                      {alreadyLinked && (
                        <span className="text-[10px] text-muted-foreground shrink-0">
                          already linked
                        </span>
                      )}
                    </button>
                  );
                })
              )}
            </div>
          </div>

          <div>
            <div className="mb-1.5 text-[10px] uppercase tracking-wide text-muted-foreground">
              Role
            </div>
            <div className="grid grid-cols-3 gap-2">
              <RoleTile
                role="primary"
                active={role === "primary"}
                onClick={() => setRole("primary")}
                label="Primary"
                hint="Main contact"
              />
              <RoleTile
                role="secondary"
                active={role === "secondary"}
                onClick={() => setRole("secondary")}
                label="Secondary"
                hint="Also a member"
              />
              <RoleTile
                role="supplier"
                active={role === "supplier"}
                onClick={() => setRole("supplier")}
                label="Supplier"
                hint="Vendor / partner"
              />
            </div>
          </div>

          {role === "primary" && target && (
            <p className="rounded-md border border-amber-400/20 bg-amber-500/[0.06] p-3 text-xs text-amber-200">
              If this contact already has a primary link elsewhere, that link will be
              atomically demoted to <em>secondary</em>. Historic tickets stay on the old
              company thanks to the frozen <code>tickets.company_id</code>.
            </p>
          )}

          {targetAlreadyLinked && (
            <p className="text-xs text-amber-300">
              This contact is already linked to this company — pick another, or change
              their role via the company's Contacts tab.
            </p>
          )}
        </div>

        <DialogFooter>
          <Button variant="ghost" onClick={onClose} disabled={addLink.isPending}>
            Cancel
          </Button>
          <Button
            onClick={() => addLink.mutate()}
            disabled={!target || targetAlreadyLinked || addLink.isPending}
          >
            {addLink.isPending ? "Linking…" : "Link contact"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

function RoleTile({
  role,
  active,
  onClick,
  label,
  hint,
}: {
  role: ContactCompanyRole;
  active: boolean;
  onClick: () => void;
  label: string;
  hint: string;
}) {
  const baseByRole: Record<ContactCompanyRole, string> = {
    primary: "border-purple-400/20 bg-purple-500/[0.04] text-purple-200/80",
    secondary: "border-sky-400/20 text-sky-200/70",
    supplier: "border-amber-400/20 text-amber-200/70",
  };
  const activeByRole: Record<ContactCompanyRole, string> = {
    primary: "border-purple-400/60 bg-purple-500/20 text-purple-100",
    secondary: "border-sky-400/50 bg-sky-500/15 text-sky-200",
    supplier: "border-amber-400/50 bg-amber-500/15 text-amber-200",
  };
  return (
    <button
      type="button"
      onClick={onClick}
      className={cn(
        "rounded-md border px-2.5 py-1.5 text-left transition-colors",
        active ? activeByRole[role] : baseByRole[role],
        !active && "hover:bg-white/[0.04]",
      )}
    >
      <div className="text-xs font-medium">{label}</div>
      <div className="text-[10px] opacity-70">{hint}</div>
    </button>
  );
}
