import * as React from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Building2 } from "lucide-react";
import {
  companyApi,
  type CompanyPickerItem,
  type ContactCompanyOption,
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
  contactId: string;
  existingCompanyIds: Set<string>;
  /// The current primary link for this contact (if any) — surfaces the
  /// demote-warning when the user picks role='primary' for a different
  /// company. Pass null when no primary exists yet.
  currentPrimary: ContactCompanyOption | null;
  onClose: () => void;
};

/// Shared "+ Link company" dialog used by the contact-detail page and the
/// ticket side-panel's Contact tab. POSTs to `/api/companies/{id}/contacts/{contactId}`
/// with a role — the server's upsert handles atomic-demote when a new primary
/// takes over from an existing one.
export function AddContactLinkDialog({
  contactId,
  existingCompanyIds,
  currentPrimary,
  onClose,
}: Props) {
  const qc = useQueryClient();
  const [search, setSearch] = React.useState("");
  const [debouncedSearch, setDebouncedSearch] = React.useState("");
  const [target, setTarget] = React.useState<CompanyPickerItem | null>(null);
  // Default to 'secondary' — the link flow never silently promotes to
  // primary, because primary-moves are intentional and deserve the
  // explicit-warning path.
  const [role, setRole] = React.useState<ContactCompanyRole>("secondary");

  React.useEffect(() => {
    const timer = setTimeout(() => setDebouncedSearch(search), 200);
    return () => clearTimeout(timer);
  }, [search]);

  const { data: matches } = useQuery({
    queryKey: ["companies", "picker", debouncedSearch],
    queryFn: () => companyApi.picker(debouncedSearch || undefined),
    placeholderData: (prev) => prev,
  });

  const addLink = useMutation({
    mutationFn: async () => {
      if (!target) throw new Error("Pick a company first.");
      await companyApi.linkContact(target.id, contactId, role);
    },
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["contact", contactId] });
      qc.invalidateQueries({ queryKey: ["contact-companies", contactId] });
      qc.invalidateQueries({ queryKey: ["contact-audit", contactId] });
      qc.invalidateQueries({ queryKey: ["contacts"] });
      toast.success(`Linked to ${target?.name}`);
      onClose();
    },
    onError: () => toast.error("Could not link company"),
  });

  const targetAlreadyLinked = target ? existingCompanyIds.has(target.id) : false;
  const willDemoteCurrentPrimary =
    role === "primary" && currentPrimary !== null && target?.id !== currentPrimary.companyId;

  return (
    <Dialog open onOpenChange={(v) => (!v ? onClose() : null)}>
      <DialogContent className="max-w-lg">
        <DialogHeader>
          <DialogTitle>Link company</DialogTitle>
          <DialogDescription>
            Pick a company and the role this contact plays in it. You can link the same
            contact to multiple companies.
          </DialogDescription>
        </DialogHeader>

        <div className="space-y-3">
          <div>
            <div className="mb-1 text-[10px] uppercase tracking-wide text-muted-foreground">
              Company
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
                  const alreadyLinked = existingCompanyIds.has(c.id);
                  const selected = target?.id === c.id;
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
                      <span className="flex items-center gap-2 truncate">
                        <Building2 className="h-3.5 w-3.5 text-muted-foreground" />
                        <span className="truncate">{c.shortName || c.name}</span>
                        <span className="font-mono text-[10px] text-muted-foreground">
                          {c.code}
                        </span>
                      </span>
                      {alreadyLinked && (
                        <span className="text-[10px] text-muted-foreground">
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

          {willDemoteCurrentPrimary && (
            <p className="rounded-md border border-amber-400/20 bg-amber-500/[0.06] p-3 text-xs text-amber-200">
              <strong>{currentPrimary?.companyName}</strong> is currently primary. Saving
              will atomically demote it to <em>secondary</em> — historic tickets stay on
              the old company thanks to the frozen <code>tickets.company_id</code>.
            </p>
          )}

          {targetAlreadyLinked && (
            <p className="text-xs text-amber-300">
              This company is already linked — pick another, or use the existing link's
              role-dropdown on the company's Contacts tab to change its role.
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
            {addLink.isPending ? "Linking…" : "Link company"}
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
