import { useEffect, useMemo, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Loader2, Search, X } from "lucide-react";
import { apiErrorMessage, zammadAdminApi, zammadDryRunApi } from "@/lib/api";
import {
  contactApi,
  companyApi,
  type CompanyPickerItem,
  type ContactCompanyRole,
} from "@/lib/ticket-api";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
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

type Props = {
  open: boolean;
  onClose: () => void;
  runId: string;
  recordId: string;
  email: string;
  /** Zammad customer id parsed out of the record's mapping snapshot.
   *  When set, the dialog pre-fills first/last name with the upstream's
   *  values via GET /api/admin/integrations/zammad/users/{id}. Null
   *  when the snapshot didn't carry an id (very old records or a
   *  contact_email-only flow). */
  zammadCustomerId: number | null;
};

/// Modal launched from a no-contact record. The email is read-only —
/// the only point of this flow is to create the missing contact for
/// the email the ticket already carries. Names + company are optional;
/// the admin can fill them later in the Contacts UI. On submit:
/// 1. POST /api/contacts (existing endpoint, agent-scoped).
/// 2. POST /api/admin/integrations/zammad/import/runs/{runId}/recheck
///    with this one record id so the row's verdict flips from
///    skipped_no_contact to whatever the resolver decides next.
/// 3. Toast + close + invalidate the records + run queries.
export function CreateContactFromZammadDialog({
  open,
  onClose,
  runId,
  recordId,
  email,
  zammadCustomerId,
}: Props) {
  const qc = useQueryClient();
  const [firstName, setFirstName] = useState("");
  const [lastName, setLastName] = useState("");
  const [companyId, setCompanyId] = useState<string | null>(null);
  const [companyRole, setCompanyRole] = useState<ContactCompanyRole>("primary");
  const [companySearch, setCompanySearch] = useState("");
  const [pickerOpen, setPickerOpen] = useState(false);
  const [namesEditedByUser, setNamesEditedByUser] = useState(false);

  // Reset form whenever the dialog opens for a new record. Without this
  // the previous record's name leaks across modals.
  useEffect(() => {
    if (open) {
      setFirstName("");
      setLastName("");
      setCompanyId(null);
      setCompanyRole("primary");
      setCompanySearch("");
      setPickerOpen(false);
      setNamesEditedByUser(false);
    }
  }, [open, recordId]);

  // Lazy fetch of the Zammad user so the dialog opens fast even on a
  // slow Zammad. The name fields stay editable while the request is in
  // flight; once it lands we only fill in fields the admin hasn't
  // touched yet (tracked by namesEditedByUser).
  const userQuery = useQuery({
    queryKey: ["integrations", "zammad", "user", zammadCustomerId] as const,
    queryFn: () =>
      zammadCustomerId !== null ? zammadAdminApi.getUser(zammadCustomerId) : null,
    enabled: open && zammadCustomerId !== null,
    staleTime: 60_000,
  });

  // Apply Zammad-side defaults once. Re-runs only if the admin hasn't
  // touched the name fields — once they do, the upstream is no longer
  // authoritative for this session.
  useEffect(() => {
    if (!open || namesEditedByUser) return;
    const u = userQuery.data;
    if (!u) return;
    if (u.firstName) setFirstName(u.firstName);
    if (u.lastName) setLastName(u.lastName);
  }, [open, namesEditedByUser, userQuery.data]);

  // The mutation does both calls back-to-back. The recheck failure is
  // not fatal — the contact still exists, the admin can manually
  // recheck. Surface as a warning toast.
  const submitMutation = useMutation({
    mutationFn: async () => {
      const created = await contactApi.create({
        email,
        firstName: firstName.trim() || undefined,
        lastName: lastName.trim() || undefined,
        companyId: companyId ?? null,
        role: companyId ? companyRole : undefined,
      });
      let rechecked = 0;
      try {
        const r = await zammadDryRunApi.recheck(runId, [recordId]);
        rechecked = r.rechecked;
      } catch (recheckErr) {
        // Don't fail the whole operation — contact is created.
        toast.warning(
          `Contact created but recheck failed: ${apiErrorMessage(recheckErr) ?? "unknown error"}. Run recheck manually.`,
        );
      }
      return { created, rechecked };
    },
    onSuccess: ({ created, rechecked }) => {
      toast.success(
        `Contact ${created.email} created. ${rechecked} record(s) re-evaluated.`,
      );
      void qc.invalidateQueries({
        queryKey: ["integrations", "zammad", "import", "run-records", runId],
      });
      void qc.invalidateQueries({
        queryKey: ["integrations", "zammad", "import", "run", runId],
      });
      onClose();
    },
    onError: (err) =>
      toast.error(apiErrorMessage(err) ?? "Could not create contact"),
  });

  return (
    <Dialog
      open={open}
      onOpenChange={(o) => {
        if (!o && !submitMutation.isPending) onClose();
      }}
    >
      <DialogContent className="sm:max-w-md">
        <DialogHeader>
          <DialogTitle>Create contact</DialogTitle>
          <DialogDescription>
            Add the missing contact so this Zammad ticket can be mapped.
            The record will be re-evaluated automatically.
          </DialogDescription>
        </DialogHeader>

        <div className="space-y-3 py-2">
          <Field label="Email">
            <Input value={email} readOnly disabled className="bg-white/[0.02] font-mono text-xs" />
          </Field>
          <div className="grid grid-cols-2 gap-2">
            <Field
              label={
                userQuery.isFetching && !namesEditedByUser
                  ? "First name (loading…)"
                  : "First name"
              }
            >
              <Input
                value={firstName}
                onChange={(e) => {
                  setFirstName(e.target.value);
                  setNamesEditedByUser(true);
                }}
                placeholder="Optional"
                autoFocus
              />
            </Field>
            <Field label="Last name">
              <Input
                value={lastName}
                onChange={(e) => {
                  setLastName(e.target.value);
                  setNamesEditedByUser(true);
                }}
                placeholder="Optional"
              />
            </Field>
          </div>
          {userQuery.data && !namesEditedByUser ? (
            <div className="-mt-2 text-[10px] text-muted-foreground/60">
              Pre-filled from Zammad — edit if needed.
            </div>
          ) : null}
          <Field label="Company (optional)">
            <CompanyPicker
              selectedId={companyId}
              search={companySearch}
              onSearchChange={setCompanySearch}
              isOpen={pickerOpen}
              setOpen={setPickerOpen}
              onSelect={(id) => {
                setCompanyId(id);
                setPickerOpen(false);
              }}
              onClear={() => {
                setCompanyId(null);
                setCompanySearch("");
              }}
            />
          </Field>
          {companyId ? (
            <Field label="Link role">
              <Select
                value={companyRole}
                onValueChange={(v) => setCompanyRole(v as ContactCompanyRole)}
              >
                <SelectTrigger className="h-9 bg-white/[0.02] text-xs">
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value="primary">
                    Primary — the contact's home company
                  </SelectItem>
                  <SelectItem value="secondary">
                    Secondary — also works there
                  </SelectItem>
                  <SelectItem value="supplier">
                    Supplier — vendor relationship
                  </SelectItem>
                </SelectContent>
              </Select>
            </Field>
          ) : null}
        </div>

        <DialogFooter>
          <Button
            variant="ghost"
            onClick={onClose}
            disabled={submitMutation.isPending}
          >
            Cancel
          </Button>
          <Button
            onClick={() => submitMutation.mutate()}
            disabled={submitMutation.isPending}
          >
            {submitMutation.isPending ? (
              <>
                <Loader2 className="mr-1.5 h-3.5 w-3.5 animate-spin" />
                Creating…
              </>
            ) : (
              "Create + recheck"
            )}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <label className="block">
      <span className="text-[10px] uppercase tracking-widest text-muted-foreground/60">
        {label}
      </span>
      <div className="mt-1">{children}</div>
    </label>
  );
}

function CompanyPicker({
  selectedId,
  search,
  onSearchChange,
  isOpen,
  setOpen,
  onSelect,
  onClear,
}: {
  selectedId: string | null;
  search: string;
  onSearchChange: (s: string) => void;
  isOpen: boolean;
  setOpen: (b: boolean) => void;
  onSelect: (id: string) => void;
  onClear: () => void;
}) {
  // Debounce search so each keystroke doesn't fire a roundtrip.
  const [debouncedSearch, setDebouncedSearch] = useState(search);
  useEffect(() => {
    const h = setTimeout(() => setDebouncedSearch(search), 200);
    return () => clearTimeout(h);
  }, [search]);

  const pickerQuery = useQuery({
    queryKey: ["companies", "picker", debouncedSearch] as const,
    queryFn: () => companyApi.picker(debouncedSearch),
    enabled: isOpen,
    staleTime: 30_000,
  });

  // Resolve the selected id to a label so the closed-state shows the
  // company name rather than the bare uuid.
  const selectedLabel = useMemo(() => {
    if (!selectedId) return null;
    return pickerQuery.data?.find((c) => c.id === selectedId)?.name ?? null;
  }, [selectedId, pickerQuery.data]);

  if (!isOpen) {
    return (
      <div className="flex items-center gap-2">
        <button
          type="button"
          onClick={() => setOpen(true)}
          className="flex-1 rounded-md border border-white/10 bg-white/[0.02] px-3 py-2 text-left text-sm text-foreground hover:border-white/20"
        >
          {selectedLabel ? (
            <span>{selectedLabel}</span>
          ) : (
            <span className="text-muted-foreground/60">
              No company — search to pick one
            </span>
          )}
        </button>
        {selectedId ? (
          <Button
            type="button"
            size="sm"
            variant="ghost"
            className="h-8 text-muted-foreground hover:text-rose-300"
            onClick={onClear}
            title="Clear company"
          >
            <X className="h-3.5 w-3.5" />
          </Button>
        ) : null}
      </div>
    );
  }

  return (
    <div className="space-y-1">
      <div className="relative">
        <Search className="pointer-events-none absolute left-2.5 top-1/2 h-3.5 w-3.5 -translate-y-1/2 text-muted-foreground/60" />
        <Input
          value={search}
          onChange={(e) => onSearchChange(e.target.value)}
          placeholder="Search active companies…"
          autoFocus
          className="pl-8"
        />
      </div>
      <PickerResults
        items={pickerQuery.data ?? []}
        loading={pickerQuery.isLoading || pickerQuery.isFetching}
        onSelect={onSelect}
        onClose={() => setOpen(false)}
      />
    </div>
  );
}

function PickerResults({
  items,
  loading,
  onSelect,
  onClose,
}: {
  items: CompanyPickerItem[];
  loading: boolean;
  onSelect: (id: string) => void;
  onClose: () => void;
}) {
  return (
    <div className="max-h-44 overflow-y-auto rounded-md border border-white/[0.06] bg-white/[0.02]">
      {loading ? (
        <div className="px-3 py-2 text-xs text-muted-foreground">Loading…</div>
      ) : items.length === 0 ? (
        <div className="px-3 py-2 text-xs text-muted-foreground">
          No matches.{" "}
          <button
            type="button"
            className="underline hover:text-foreground"
            onClick={onClose}
          >
            Close
          </button>
        </div>
      ) : (
        <ul className="divide-y divide-white/[0.04]">
          {items.map((c) => (
            <li key={c.id}>
              <button
                type="button"
                onClick={() => onSelect(c.id)}
                className={cn(
                  "w-full px-3 py-2 text-left text-xs hover:bg-white/[0.03]",
                )}
              >
                <div className="text-foreground">{c.name}</div>
                {c.code ? (
                  <div className="text-[10px] text-muted-foreground/60">
                    {c.code}
                  </div>
                ) : null}
              </button>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
