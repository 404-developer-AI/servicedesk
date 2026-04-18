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
  ticketId: string;
  contactId: string;
  onClose: () => void;
  onAssigned: () => void;
  submit: (companyId: string, linkAsSupplier: boolean) => Promise<void>;
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
  onClose,
  onAssigned,
  submit,
}: Props) {
  const [search, setSearch] = React.useState("");
  const [selectedId, setSelectedId] = React.useState<string | null>(null);
  const [selectedName, setSelectedName] = React.useState<string | null>(null);
  const [linkAsSupplier, setLinkAsSupplier] = React.useState(false);
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
  // different ticket starts clean.
  React.useEffect(() => {
    if (!open) {
      setSearch("");
      setSelectedId(null);
      setSelectedName(null);
      setLinkAsSupplier(false);
      setSaving(false);
      setError(null);
    }
  }, [open, ticketId]);

  const selectContactOption = (option: ContactCompanyOption) => {
    setSelectedId(option.companyId);
    setSelectedName(option.companyName);
    // Supplier-link already exists for this option if role is 'supplier'; the
    // learn-flow only makes sense when we're about to create a *new* bond.
    setLinkAsSupplier(false);
  };
  const selectSearchResult = (company: CompanyPickerItem) => {
    setSelectedId(company.id);
    setSelectedName(company.name);
    setLinkAsSupplier(false);
  };

  const selectedIsInContactLinks = React.useMemo(
    () => contactOptions?.some((o) => o.companyId === selectedId) ?? false,
    [contactOptions, selectedId],
  );
  const canOfferSupplierLink = !!selectedId && !selectedIsInContactLinks;

  const handleSubmit = async () => {
    if (!selectedId || saving) return;
    setSaving(true);
    setError(null);
    try {
      await submit(selectedId, canOfferSupplierLink && linkAsSupplier);
      onAssigned();
    } catch (e) {
      setError(e instanceof Error ? e.message : "Kon ticket niet toewijzen.");
      setSaving(false);
    }
  };

  return (
    <Dialog open={open} onOpenChange={(o) => !o && !saving && onClose()}>
      <DialogContent className="max-w-lg">
        <DialogHeader>
          <DialogTitle className="flex items-center gap-2">
            <Building2 className="h-4 w-4 text-primary" />
            Wijs een company toe aan dit ticket
          </DialogTitle>
          <DialogDescription>
            Deze contact is niet éénduidig aan een company gekoppeld — kies
            aan welke company dit ticket toegewezen wordt.
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

        <div className="max-h-64 overflow-y-auto rounded-md border border-white/10 bg-white/[0.02] divide-y divide-white/5">
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

        {canOfferSupplierLink && (
          <label className="flex items-start gap-2 text-xs text-muted-foreground cursor-pointer select-none">
            <input
              type="checkbox"
              checked={linkAsSupplier}
              onChange={(e) => setLinkAsSupplier(e.target.checked)}
              className="mt-0.5 h-3.5 w-3.5 accent-primary"
            />
            <span>
              Koppel dit contact ook als <strong className="font-medium text-foreground/80">supplier</strong>{" "}
              aan <span className="text-foreground/80">{selectedName}</span> —
              volgende mail van dit contact kent deze company dan automatisch.
            </span>
          </label>
        )}

        {error && (
          <div className="rounded-md border border-red-500/30 bg-red-500/10 px-3 py-2 text-xs text-red-200">
            {error}
          </div>
        )}

        <DialogFooter>
          <Button variant="ghost" onClick={onClose} disabled={saving}>
            Annuleer
          </Button>
          <Button onClick={handleSubmit} disabled={!selectedId || saving}>
            {saving ? (
              "Toewijzen..."
            ) : (
              <>
                <CheckCircle2 className="h-4 w-4 mr-1.5" />
                Toewijzen
                {selectedName && (
                  <span className="ml-1 text-primary-foreground/70">
                    aan {selectedName}
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
        !disabled && "hover:bg-white/[0.04]",
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
