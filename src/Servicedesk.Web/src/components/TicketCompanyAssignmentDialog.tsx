import * as React from "react";
import { useQuery } from "@tanstack/react-query";
import { Search, Building2, CheckCircle2 } from "lucide-react";
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
import { contactApi, companyApi } from "@/lib/ticket-api";
import type {
  ContactCompanyOption,
  ContactCompanyRole,
  CompanyPickerItem,
} from "@/lib/ticket-api";

type Props = {
  open: boolean;
  /// Only set in 'assign' mode where a ticket already exists. The
  /// create-time popup (mode='create') leaves this undefined — the
  /// callback returns the picked company to the drawer, which sends
  /// it along with POST /api/tickets.
  ticketId?: string;
  contactId: string;
  /// 'assign' (default) = post-create reassignment via PATCH /api/tickets/{id}/company.
  /// 'create' = pre-create selection used by NewTicketDrawer; copy changes
  /// from "wijs toe" → "selecteer voor dit ticket" so the agent doesn't
  /// think the action commits before submit.
  mode?: "create" | "assign";
  onClose: () => void;
  onAssigned: () => void;
  /// Receives the agent's choice. In 'assign' mode the parent calls
  /// the API and resolves on success; in 'create' mode the parent
  /// stashes the selection and the API call happens later via
  /// POST /api/tickets. `companyName` is included so the parent can
  /// render a readable badge without a second round-trip. `newLinkRole`
  /// is the role for a brand-new contact_companies row when the
  /// picked company isn't yet on the contact's link list; null when
  /// the company is already linked (no new row needed).
  submit: (
    companyId: string,
    companyName: string,
    newLinkRole: ContactCompanyRole | null,
  ) => Promise<void>;
};

const ROLE_BADGE: Record<ContactCompanyRole, { label: string; className: string }> = {
  primary: {
    label: "Primary",
    className: "bg-primary/20 border-primary/40 text-primary-foreground/90",
  },
  secondary: {
    label: "Secondary",
    className: "bg-sky-500/15 border-sky-400/30 text-sky-200",
  },
  supplier: {
    label: "Supplier",
    className: "bg-amber-500/15 border-amber-400/30 text-amber-200",
  },
};

/// v0.0.9 ToDo #4: prompts an agent to explicitly pick which company a ticket
/// belongs to when the intake decision tree landed on `awaiting`. Explicit
/// choice required — no silent default, so vendor-mails and ambiguous
/// secondaries can never silently bind.
export function TicketCompanyAssignmentDialog({
  open,
  ticketId,
  contactId,
  mode = "assign",
  onClose,
  onAssigned,
  submit,
}: Props) {
  const isCreateMode = mode === "create";
  const [search, setSearch] = React.useState("");
  const [selectedId, setSelectedId] = React.useState<string | null>(null);
  const [selectedName, setSelectedName] = React.useState<string | null>(null);
  // v0.0.51 — agent picks the role for a brand-new contact_companies
  // link when the chosen company isn't on the contact's list yet.
  // Null = no choice made yet (save is blocked); also null when the
  // picked company is already linked (no new row needed).
  const [selectedRole, setSelectedRole] = React.useState<ContactCompanyRole | null>(null);
  const [saving, setSaving] = React.useState(false);
  const [error, setError] = React.useState<string | null>(null);

  const trimmed = search.trim();
  const isSearching = trimmed.length > 0;

  const { data: contactOptions, isLoading: loadingDefault } = useQuery({
    queryKey: ["contact-companies", contactId],
    queryFn: () => contactApi.listCompanies(contactId),
    enabled: open,
    staleTime: 60_000,
  });

  const { data: searchResults, isFetching: searching } = useQuery({
    queryKey: ["company-picker", trimmed],
    queryFn: () => companyApi.picker(trimmed),
    enabled: open && isSearching,
    staleTime: 30_000,
  });

  // Reset ephemeral state whenever the dialog closes so the next open for a
  // different ticket starts clean. In create-mode `ticketId` is undefined
  // so we key the reset on `contactId` — picking a different requester in
  // the drawer should clear any stale company selection.
  React.useEffect(() => {
    if (!open) {
      setSearch("");
      setSelectedId(null);
      setSelectedName(null);
      setSelectedRole(null);
      setSaving(false);
      setError(null);
    }
  }, [open, ticketId, contactId]);

  const selectContactOption = (option: ContactCompanyOption) => {
    setSelectedId(option.companyId);
    setSelectedName(option.companyName);
    // Existing link → no new contact_companies row needed; role is
    // whatever it already is, the dialog doesn't change it here.
    setSelectedRole(null);
  };
  const selectSearchResult = (company: CompanyPickerItem) => {
    setSelectedId(company.id);
    setSelectedName(company.name);
    // Picked an unlinked company: agent must pick a role next.
    setSelectedRole(null);
  };

  const selectedIsInContactLinks = React.useMemo(
    () => contactOptions?.some((o) => o.companyId === selectedId) ?? false,
    [contactOptions, selectedId],
  );
  const requiresRoleChoice = !!selectedId && !selectedIsInContactLinks;
  const existingPrimary = React.useMemo(
    () => contactOptions?.find((o) => o.role === "primary") ?? null,
    [contactOptions],
  );
  const primaryBlocked = !!existingPrimary;
  // Submit is gated by mode-specific rules: an existing-link pick is
  // ready as soon as a company is selected; a new-link pick also
  // needs a role, and 'primary' is blocked when one already exists.
  const canSubmit =
    !!selectedId &&
    !saving &&
    (!requiresRoleChoice ||
      (selectedRole !== null && !(selectedRole === "primary" && primaryBlocked)));

  const handleSubmit = async () => {
    if (!canSubmit || !selectedId) return;
    setSaving(true);
    setError(null);
    try {
      const newLinkRole: ContactCompanyRole | null = requiresRoleChoice ? selectedRole : null;
      await submit(selectedId, selectedName ?? "", newLinkRole);
      onAssigned();
    } catch (e) {
      setError(
        e instanceof Error
          ? e.message
          : isCreateMode
            ? "Could not select company."
            : "Could not assign ticket.",
      );
      setSaving(false);
    }
  };

  return (
    <Dialog open={open} onOpenChange={(o) => !o && !saving && onClose()}>
      <DialogContent className="max-w-lg">
        <DialogHeader>
          <DialogTitle className="flex items-center gap-2">
            <Building2 className="h-4 w-4 text-primary" />
            {isCreateMode
              ? "Select a company for this ticket"
              : "Assign a company to this ticket"}
          </DialogTitle>
          <DialogDescription>
            {isCreateMode
              ? "Pick which company this ticket belongs to. The contact's existing links are listed first; you can also search for any other company."
              : "This contact is not unambiguously linked to one company — pick which company this ticket belongs to."}
          </DialogDescription>
        </DialogHeader>

        <div className="relative">
          <Search className="absolute left-2.5 top-1/2 -translate-y-1/2 h-4 w-4 text-muted-foreground/60" />
          <Input
            autoFocus
            value={search}
            onChange={(e) => setSearch(e.target.value)}
            placeholder="Zoek in alle actieve companies..."
            className="pl-8"
          />
        </div>

        <div className="max-h-64 overflow-y-auto rounded-md border border-glass bg-glass divide-y divide-glass">
          {!isSearching && (
            <>
              {loadingDefault && (
                <RowSkeleton label="Contact-links laden..." />
              )}
              {!loadingDefault && (contactOptions?.length ?? 0) === 0 && (
                <EmptyRow label="Geen bestaande company-links voor dit contact. Typ hierboven om te zoeken." />
              )}
              {contactOptions?.map((o) => (
                <OptionRow
                  key={o.linkId}
                  selected={selectedId === o.companyId}
                  onClick={() => selectContactOption(o)}
                  disabled={!o.companyIsActive}
                >
                  <div className="flex-1 min-w-0">
                    <div className="flex items-center gap-2">
                      <span className="truncate font-medium">{o.companyName}</span>
                      <RoleBadge role={o.role} />
                      {!o.companyIsActive && (
                        <span className="text-[10px] uppercase tracking-wider text-muted-foreground/60">
                          inactief
                        </span>
                      )}
                    </div>
                    {o.companyCode && (
                      <div className="text-[11px] font-mono text-muted-foreground/70">
                        {o.companyCode}
                      </div>
                    )}
                  </div>
                </OptionRow>
              ))}
            </>
          )}

          {isSearching && (
            <>
              {searching && <RowSkeleton label="Zoeken..." />}
              {!searching && (searchResults?.length ?? 0) === 0 && (
                <EmptyRow label={`Geen actieve companies gevonden voor "${trimmed}".`} />
              )}
              {searchResults?.map((c) => (
                <OptionRow
                  key={c.id}
                  selected={selectedId === c.id}
                  onClick={() => selectSearchResult(c)}
                >
                  <div className="flex-1 min-w-0">
                    <div className="truncate font-medium">{c.name}</div>
                    {c.code && (
                      <div className="text-[11px] font-mono text-muted-foreground/70">
                        {c.code}
                      </div>
                    )}
                  </div>
                </OptionRow>
              ))}
            </>
          )}
        </div>

        {requiresRoleChoice && (
          <div className="space-y-2">
            <div className="text-xs text-muted-foreground">
              <span className="text-foreground/80">{selectedName}</span> is not yet
              linked to this contact. Pick which role to give it:
            </div>
            <div className="flex gap-2">
              {(["primary", "secondary", "supplier"] as ContactCompanyRole[]).map((role) => {
                const meta = ROLE_BADGE[role];
                const disabled = role === "primary" && primaryBlocked;
                const active = selectedRole === role;
                return (
                  <button
                    key={role}
                    type="button"
                    onClick={() => !disabled && setSelectedRole(role)}
                    disabled={disabled}
                    title={
                      disabled
                        ? `This contact already has a primary (${existingPrimary?.companyName}). Change it on the contact page first.`
                        : undefined
                    }
                    className={cn(
                      "flex-1 rounded-md border px-2.5 py-1.5 text-xs font-medium uppercase tracking-wider transition-colors",
                      disabled && "cursor-not-allowed opacity-40",
                      !disabled && !active && "border-glass bg-glass hover:bg-glass-hover",
                      active && meta.className,
                    )}
                  >
                    {meta.label}
                  </button>
                );
              })}
            </div>
            {primaryBlocked && selectedRole !== "primary" && (
              <p className="text-[11px] text-muted-foreground/70">
                Primary is unavailable — already on {existingPrimary?.companyName}.
              </p>
            )}
          </div>
        )}

        {error && (
          <div className="rounded-md border border-red-500/30 bg-red-500/10 px-3 py-2 text-xs text-red-200">
            {error}
          </div>
        )}

        <DialogFooter>
          <Button variant="ghost" onClick={onClose} disabled={saving}>
            Cancel
          </Button>
          <Button onClick={handleSubmit} disabled={!canSubmit}>
            {saving ? (
              isCreateMode ? "Selecting…" : "Assigning…"
            ) : (
              <>
                <CheckCircle2 className="h-4 w-4 mr-1.5" />
                {isCreateMode ? "Select" : "Assign"}
                {selectedName && (
                  <span className="ml-1 text-primary-foreground/70">
                    {selectedName}
                  </span>
                )}
              </>
            )}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

function OptionRow({
  selected,
  disabled,
  onClick,
  children,
}: {
  selected: boolean;
  disabled?: boolean;
  onClick: () => void;
  children: React.ReactNode;
}) {
  return (
    <button
      type="button"
      onClick={disabled ? undefined : onClick}
      disabled={disabled}
      className={cn(
        "flex w-full items-center gap-3 px-3 py-2 text-left text-sm transition-colors",
        disabled && "cursor-not-allowed opacity-50",
        !disabled && "hover:bg-glass-hover",
        selected && "bg-primary/10 hover:bg-primary/15",
      )}
    >
      {children}
      {selected && <CheckCircle2 className="h-4 w-4 text-primary shrink-0" />}
    </button>
  );
}

function RoleBadge({ role }: { role: ContactCompanyRole }) {
  const meta = ROLE_BADGE[role];
  return (
    <span
      className={cn(
        "inline-flex items-center rounded border px-1.5 py-0.5 text-[10px] font-medium uppercase tracking-wider",
        meta.className,
      )}
    >
      {meta.label}
    </span>
  );
}

function RowSkeleton({ label }: { label: string }) {
  return (
    <div className="px-3 py-4 text-xs text-muted-foreground/60 italic">{label}</div>
  );
}

function EmptyRow({ label }: { label: string }) {
  return (
    <div className="px-3 py-4 text-xs text-muted-foreground/60">{label}</div>
  );
}
