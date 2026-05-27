import * as React from "react";
import { Link, useNavigate, useSearch } from "@tanstack/react-router";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import {
  ArrowLeft,
  ExternalLink,
  Link2,
  RefreshCw,
  Search,
  Trash2,
} from "lucide-react";
import {
  ApiError,
  adsolutApi,
  type AdsolutCoverageCompaniesBucket,
  type AdsolutCoverageCompanyRow,
  type AdsolutCoverageContactRow,
  type AdsolutCoverageContactsBucket,
} from "@/lib/api";
import { companyApi, contactApi, type Company, type Contact } from "@/lib/ticket-api";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { CompanyEditDialog } from "@/components/CompanyEditDialog";
import { ContactFormDialog } from "@/components/ContactFormDialog";
import { cn } from "@/lib/utils";

type Tab = "companies" | "contacts";

const COMPANIES_BUCKETS: { value: AdsolutCoverageCompaniesBucket; label: string }[] = [
  { value: "sd-only", label: "SD only" },
  { value: "drift", label: "Drift" },
];

const CONTACTS_BUCKETS: { value: AdsolutCoverageContactsBucket; label: string }[] = [
  { value: "links-unsynced", label: "Links — unsynced" },
  { value: "links-drift", label: "Links — drift" },
  { value: "pure-sd", label: "Pure SD" },
];

const PAGE_SIZE = 50;

function isCompaniesBucket(v: string | undefined): v is AdsolutCoverageCompaniesBucket {
  return v === "sd-only" || v === "drift";
}
function isContactsBucket(v: string | undefined): v is AdsolutCoverageContactsBucket {
  return v === "links-unsynced" || v === "links-drift" || v === "pure-sd";
}

function fmtDate(iso: string | null | undefined) {
  if (!iso) return "—";
  try {
    return new Intl.DateTimeFormat(undefined, {
      year: "numeric",
      month: "short",
      day: "2-digit",
      hour: "2-digit",
      minute: "2-digit",
    }).format(new Date(iso));
  } catch {
    return iso;
  }
}

const isUuid = (v: string) =>
  /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(v.trim());

export function AdsolutCoveragePage() {
  const navigate = useNavigate();
  const rawSearch = useSearch({ strict: false }) as {
    tab?: "companies" | "contacts";
    bucket?: string;
    search?: string;
    page?: number;
  };
  const tab: Tab = rawSearch.tab === "contacts" ? "contacts" : "companies";

  // Bucket default per tab; admins arrive via the tile's deep-link with
  // an explicit bucket already chosen.
  const companyBucket: AdsolutCoverageCompaniesBucket =
    tab === "companies" && isCompaniesBucket(rawSearch.bucket) ? rawSearch.bucket : "sd-only";
  const contactBucket: AdsolutCoverageContactsBucket =
    tab === "contacts" && isContactsBucket(rawSearch.bucket) ? rawSearch.bucket : "links-unsynced";

  const page = Math.max(1, rawSearch.page ?? 1);
  const [searchInput, setSearchInput] = React.useState<string>(rawSearch.search ?? "");

  // Debounce search input → URL state so a fast typer doesn't fire a
  // request per keystroke.
  React.useEffect(() => {
    const t = window.setTimeout(() => {
      const current = rawSearch.search ?? "";
      if (searchInput !== current) {
        navigate({
          to: "/settings/integrations/adsolut/coverage",
          search: {
            tab,
            bucket: rawSearch.bucket,
            search: searchInput.trim() || undefined,
            page: 1,
          },
        });
      }
    }, 250);
    return () => window.clearTimeout(t);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [searchInput]);

  const setTab = (next: Tab) => {
    navigate({
      to: "/settings/integrations/adsolut/coverage",
      search: {
        tab: next,
        bucket: next === "companies" ? "sd-only" : "links-unsynced",
        search: undefined,
        page: 1,
      },
    });
  };

  const setBucket = (bucketValue: string) => {
    navigate({
      to: "/settings/integrations/adsolut/coverage",
      search: {
        tab,
        bucket: bucketValue,
        search: searchInput.trim() || undefined,
        page: 1,
      },
    });
  };

  const setPage = (next: number) => {
    navigate({
      to: "/settings/integrations/adsolut/coverage",
      search: {
        tab,
        bucket: rawSearch.bucket,
        search: searchInput.trim() || undefined,
        page: next,
      },
    });
  };

  return (
    <div className="flex flex-col gap-6">
      <header className="flex items-start justify-between gap-4">
        <div className="space-y-2">
          <Link
            to="/settings/integrations/adsolut"
            className="inline-flex items-center gap-1.5 text-xs text-muted-foreground hover:text-foreground"
          >
            <ArrowLeft className="h-3.5 w-3.5" />
            Back to Adsolut
          </Link>
          <h1 className="text-display-md font-semibold text-foreground">
            Sync coverage
          </h1>
          <p className="max-w-2xl text-sm text-muted-foreground">
            Surface the gaps between SD's local universe and Adsolut. Each
            bucket shows rows that exist locally but are not (or no longer)
            reflected upstream. Resolve a row by editing it, linking it to
            an existing Adsolut UUID, deleting it locally, or — for drift
            rows — force-pushing it now.
          </p>
        </div>
        <Badge className="border border-glass bg-glass text-xs font-normal text-muted-foreground">
          Admin only
        </Badge>
      </header>

      <div className="flex flex-wrap items-center justify-between gap-3 rounded-lg border border-glass-strong bg-glass p-3">
        <div
          role="tablist"
          aria-label="Coverage tab"
          className="inline-flex rounded-md border border-glass-strong bg-glass p-0.5 text-xs"
        >
          {(["companies", "contacts"] as const).map((t) => (
            <button
              key={t}
              type="button"
              role="tab"
              aria-selected={tab === t}
              onClick={() => setTab(t)}
              className={cn(
                "rounded px-3 py-1 capitalize transition-colors",
                tab === t
                  ? "bg-glass-strong text-foreground shadow-sm"
                  : "text-muted-foreground hover:text-foreground",
              )}
            >
              {t}
            </button>
          ))}
        </div>

        <div
          role="tablist"
          aria-label="Bucket"
          className="inline-flex rounded-md border border-glass-strong bg-glass p-0.5 text-xs"
        >
          {(tab === "companies" ? COMPANIES_BUCKETS : CONTACTS_BUCKETS).map((b) => {
            const active = (rawSearch.bucket ?? "") === b.value;
            return (
              <button
                key={b.value}
                type="button"
                role="tab"
                aria-selected={active}
                onClick={() => setBucket(b.value)}
                className={cn(
                  "rounded px-3 py-1 transition-colors",
                  active
                    ? "bg-glass-strong text-foreground shadow-sm"
                    : "text-muted-foreground hover:text-foreground",
                )}
              >
                {b.label}
              </button>
            );
          })}
        </div>

        <div className="flex flex-1 items-center justify-end gap-2 sm:flex-none">
          <Search className="h-3.5 w-3.5 text-muted-foreground" />
          <Input
            value={searchInput}
            onChange={(e) => setSearchInput(e.target.value)}
            placeholder={tab === "companies" ? "Name, code, email, VAT…" : "Email, name, company…"}
            className="max-w-xs"
          />
        </div>
      </div>

      {tab === "companies" ? (
        <CompaniesTable
          bucket={companyBucket}
          search={rawSearch.search ?? ""}
          page={page}
          onPageChange={setPage}
        />
      ) : (
        <ContactsTable
          bucket={contactBucket}
          search={rawSearch.search ?? ""}
          page={page}
          onPageChange={setPage}
        />
      )}
    </div>
  );
}

// ---- Companies tab --------------------------------------------------

type CompaniesTableProps = {
  bucket: AdsolutCoverageCompaniesBucket;
  search: string;
  page: number;
  onPageChange: (next: number) => void;
};

function CompaniesTable({ bucket, search, page, onPageChange }: CompaniesTableProps) {
  const qc = useQueryClient();
  const list = useQuery({
    queryKey: ["integrations", "adsolut", "coverage", "companies", bucket, search, page],
    queryFn: () => adsolutApi.coverageCompanies(bucket, search, page, PAGE_SIZE),
    placeholderData: (prev) => prev,
  });

  const [selected, setSelected] = React.useState<Set<string>>(new Set());
  const [editTarget, setEditTarget] = React.useState<Company | null>(null);
  const [linkTarget, setLinkTarget] = React.useState<AdsolutCoverageCompanyRow | null>(null);
  const [deleteTarget, setDeleteTarget] = React.useState<AdsolutCoverageCompanyRow | null>(
    null,
  );

  // Reset multi-select on bucket / page change so a stale selection from a
  // previous view doesn't leak into a force-push-many click.
  React.useEffect(() => {
    setSelected(new Set());
  }, [bucket, page, search]);

  const invalidateAll = () => {
    qc.invalidateQueries({ queryKey: ["integrations", "adsolut", "coverage"] });
    qc.invalidateQueries({ queryKey: ["companies"] });
  };

  const forcePush = useMutation({
    mutationFn: (id: string) => adsolutApi.coverageForcePushCompany(id),
    onSuccess: (r, id) => {
      toast.success(`Force-pushed ${r.outcome.toLowerCase()} (${shortId(id)})`);
      invalidateAll();
    },
    onError: (err) => {
      toast.error(err instanceof ApiError ? `Force-push failed (${err.status})` : "Force-push failed");
    },
  });

  const removeCompany = useMutation({
    mutationFn: (id: string) => companyApi.remove(id),
    onSuccess: () => {
      toast.success("Company deleted");
      invalidateAll();
      setDeleteTarget(null);
    },
    onError: (err) => {
      if (err instanceof ApiError && err.status === 409) {
        toast.error("Company has live tickets — reassign first.");
      } else {
        toast.error("Delete failed");
      }
    },
  });

  const total = list.data?.total ?? 0;
  const totalPages = Math.max(1, Math.ceil(total / PAGE_SIZE));
  const items = list.data?.items ?? [];
  const allSelected = items.length > 0 && items.every((i) => selected.has(i.id));

  return (
    <section className="rounded-lg border border-glass-strong bg-glass p-3">
      {list.isLoading ? (
        <Skeleton className="h-32 w-full" />
      ) : items.length === 0 ? (
        <div className="rounded-md border border-emerald-400/20 bg-emerald-500/[0.04] p-4 text-xs text-emerald-300">
          Nothing in this bucket — all companies are covered.
        </div>
      ) : (
        <div className="space-y-2">
          {bucket === "drift" && (
            <div className="flex flex-wrap items-center gap-2 rounded-md border border-amber-400/20 bg-amber-500/[0.04] px-3 py-2 text-[11px] text-amber-200">
              <span>{selected.size} selected</span>
              <Button
                size="sm"
                variant="ghost"
                disabled={selected.size === 0}
                onClick={async () => {
                  for (const id of selected) {
                    await forcePush.mutateAsync(id).catch(() => undefined);
                  }
                  setSelected(new Set());
                }}
                className="h-7 text-[11px]"
              >
                <RefreshCw className="mr-1 h-3 w-3" />
                Force sync selected
              </Button>
            </div>
          )}

          <table className="w-full text-xs">
            <thead className="text-muted-foreground/70">
              <tr className="border-b border-glass">
                {bucket === "drift" && (
                  <th className="w-8 px-2 py-2 text-left">
                    <input
                      type="checkbox"
                      checked={allSelected}
                      onChange={(e) => {
                        const s = new Set(selected);
                        if (e.target.checked) {
                          items.forEach((i) => s.add(i.id));
                        } else {
                          items.forEach((i) => s.delete(i.id));
                        }
                        setSelected(s);
                      }}
                    />
                  </th>
                )}
                <th className="px-2 py-2 text-left font-medium">Name</th>
                <th className="px-2 py-2 text-left font-medium">Code</th>
                <th className="px-2 py-2 text-left font-medium">Email</th>
                <th className="px-2 py-2 text-left font-medium">Adsolut</th>
                <th className="px-2 py-2 text-left font-medium">Updated</th>
                <th className="px-2 py-2 text-right font-medium">Actions</th>
              </tr>
            </thead>
            <tbody>
              {items.map((row) => (
                <tr
                  key={row.id}
                  className="border-b border-glass hover:bg-glass-hover"
                >
                  {bucket === "drift" && (
                    <td className="px-2 py-2">
                      <input
                        type="checkbox"
                        checked={selected.has(row.id)}
                        onChange={(e) => {
                          const s = new Set(selected);
                          if (e.target.checked) s.add(row.id);
                          else s.delete(row.id);
                          setSelected(s);
                        }}
                      />
                    </td>
                  )}
                  <td className="px-2 py-2">
                    <Link
                      to="/companies/$companyId"
                      params={{ companyId: row.id }}
                      className="text-foreground hover:underline"
                    >
                      {row.name}
                    </Link>
                  </td>
                  <td className="px-2 py-2 font-mono text-muted-foreground">
                    {row.code ?? "—"}
                  </td>
                  <td className="px-2 py-2 text-muted-foreground">{row.email ?? "—"}</td>
                  <td className="px-2 py-2">
                    {row.adsolutId ? (
                      <span className="inline-flex items-center gap-1 rounded-full border border-emerald-400/30 bg-emerald-500/10 px-2 py-0.5 text-[10px] text-emerald-300">
                        Linked{row.adsolutNumber ? ` · ${row.adsolutNumber}` : ""}
                      </span>
                    ) : (
                      <span className="inline-flex items-center gap-1 rounded-full border border-glass-strong bg-glass px-2 py-0.5 text-[10px] text-muted-foreground">
                        Not linked
                      </span>
                    )}
                  </td>
                  <td className="px-2 py-2 text-muted-foreground tabular-nums">
                    {fmtDate(row.updatedUtc)}
                  </td>
                  <td className="px-2 py-2 text-right">
                    <div className="flex items-center justify-end gap-1">
                      <Button
                        size="sm"
                        variant="ghost"
                        className="h-7 px-2 text-[11px]"
                        onClick={async () => {
                          try {
                            const detail = await companyApi.get(row.id);
                            // CompanyDetail is a superset of Company; cast
                            // through unknown so the dialog's prop type lines
                            // up without forcing every CompanyDetail caller
                            // to slice the same fields.
                            setEditTarget(detail as unknown as Company);
                          } catch {
                            toast.error("Could not load company");
                          }
                        }}
                      >
                        Edit
                      </Button>
                      {bucket === "sd-only" && (
                        <Button
                          size="sm"
                          variant="ghost"
                          className="h-7 px-2 text-[11px]"
                          onClick={() => setLinkTarget(row)}
                        >
                          <Link2 className="mr-1 h-3 w-3" />
                          Link
                        </Button>
                      )}
                      {bucket === "drift" && (
                        <Button
                          size="sm"
                          variant="ghost"
                          className="h-7 px-2 text-[11px]"
                          disabled={forcePush.isPending}
                          onClick={() => forcePush.mutate(row.id)}
                        >
                          <RefreshCw className="mr-1 h-3 w-3" />
                          Force sync
                        </Button>
                      )}
                      <Button
                        size="sm"
                        variant="ghost"
                        className="h-7 px-2 text-[11px] text-rose-300 hover:text-rose-200"
                        onClick={() => setDeleteTarget(row)}
                      >
                        <Trash2 className="h-3 w-3" />
                      </Button>
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>

          <PageNav page={page} totalPages={totalPages} onPageChange={onPageChange} total={total} />
        </div>
      )}

      <CompanyEditDialog
        open={!!editTarget}
        company={editTarget}
        onClose={() => {
          setEditTarget(null);
          invalidateAll();
        }}
      />

      {linkTarget && (
        <LinkCompanyDialog
          open
          row={linkTarget}
          onClose={() => setLinkTarget(null)}
          onLinked={() => {
            setLinkTarget(null);
            invalidateAll();
          }}
        />
      )}

      {deleteTarget && (
        <Dialog open onOpenChange={(v) => !v && setDeleteTarget(null)}>
          <DialogContent>
            <DialogHeader>
              <DialogTitle>Delete company</DialogTitle>
              <DialogDescription>
                Soft-delete <span className="font-medium">{deleteTarget.name}</span>? Tickets
                referencing this company stay readable; live tickets block the deletion.
              </DialogDescription>
            </DialogHeader>
            <DialogFooter>
              <Button variant="ghost" onClick={() => setDeleteTarget(null)}>
                Cancel
              </Button>
              <Button
                onClick={() => removeCompany.mutate(deleteTarget.id)}
                disabled={removeCompany.isPending}
                className="bg-rose-500/80 hover:bg-rose-500"
              >
                Delete
              </Button>
            </DialogFooter>
          </DialogContent>
        </Dialog>
      )}
    </section>
  );
}

// ---- Contacts tab ---------------------------------------------------

type ContactsTableProps = {
  bucket: AdsolutCoverageContactsBucket;
  search: string;
  page: number;
  onPageChange: (next: number) => void;
};

function ContactsTable({ bucket, search, page, onPageChange }: ContactsTableProps) {
  const qc = useQueryClient();
  const list = useQuery({
    queryKey: ["integrations", "adsolut", "coverage", "contacts", bucket, search, page],
    queryFn: () => adsolutApi.coverageContacts(bucket, search, page, PAGE_SIZE),
    placeholderData: (prev) => prev,
  });

  const [selected, setSelected] = React.useState<Set<string>>(new Set());
  const [editContact, setEditContact] = React.useState<Contact | null>(null);
  const [linkTarget, setLinkTarget] = React.useState<AdsolutCoverageContactRow | null>(null);
  const [deleteTarget, setDeleteTarget] = React.useState<AdsolutCoverageContactRow | null>(
    null,
  );

  React.useEffect(() => {
    setSelected(new Set());
  }, [bucket, page, search]);

  const invalidateAll = () => {
    qc.invalidateQueries({ queryKey: ["integrations", "adsolut", "coverage"] });
    qc.invalidateQueries({ queryKey: ["contacts"] });
    qc.invalidateQueries({ queryKey: ["company"] });
  };

  const forcePush = useMutation({
    mutationFn: (linkId: string) => adsolutApi.coverageForcePushContact(linkId),
    onSuccess: (r, linkId) => {
      toast.success(`Force-pushed ${r.outcome.toLowerCase()} (${shortId(linkId)})`);
      invalidateAll();
    },
    onError: (err) => {
      toast.error(err instanceof ApiError ? `Force-push failed (${err.status})` : "Force-push failed");
    },
  });

  // No contact-soft-delete endpoint — surface via the existing detail page.
  // Pure-SD bucket usually wants Edit (link to a company) more than Delete.

  const total = list.data?.total ?? 0;
  const totalPages = Math.max(1, Math.ceil(total / PAGE_SIZE));
  const items = list.data?.items ?? [];

  const driftBucket = bucket === "links-drift";
  const linkableLinkBucket = bucket === "links-unsynced";

  return (
    <section className="rounded-lg border border-glass-strong bg-glass p-3">
      {list.isLoading ? (
        <Skeleton className="h-32 w-full" />
      ) : items.length === 0 ? (
        <div className="rounded-md border border-emerald-400/20 bg-emerald-500/[0.04] p-4 text-xs text-emerald-300">
          Nothing in this bucket — all contacts are covered.
        </div>
      ) : (
        <div className="space-y-2">
          {driftBucket && (
            <div className="flex flex-wrap items-center gap-2 rounded-md border border-amber-400/20 bg-amber-500/[0.04] px-3 py-2 text-[11px] text-amber-200">
              <span>{selected.size} selected</span>
              <Button
                size="sm"
                variant="ghost"
                disabled={selected.size === 0}
                onClick={async () => {
                  for (const id of selected) {
                    await forcePush.mutateAsync(id).catch(() => undefined);
                  }
                  setSelected(new Set());
                }}
                className="h-7 text-[11px]"
              >
                <RefreshCw className="mr-1 h-3 w-3" />
                Force sync selected
              </Button>
            </div>
          )}

          <table className="w-full text-xs">
            <thead className="text-muted-foreground/70">
              <tr className="border-b border-glass">
                {driftBucket && <th className="w-8 px-2 py-2 text-left"></th>}
                <th className="px-2 py-2 text-left font-medium">Name</th>
                <th className="px-2 py-2 text-left font-medium">Email</th>
                <th className="px-2 py-2 text-left font-medium">Company</th>
                <th className="px-2 py-2 text-left font-medium">Adsolut</th>
                <th className="px-2 py-2 text-left font-medium">Updated</th>
                <th className="px-2 py-2 text-right font-medium">Actions</th>
              </tr>
            </thead>
            <tbody>
              {items.map((row) => {
                const fullName =
                  `${row.firstName} ${row.lastName}`.trim() || "(no name)";
                const linkSelected = row.linkId ? selected.has(row.linkId) : false;
                return (
                  <tr
                    key={row.linkId ?? row.contactId}
                    className="border-b border-glass hover:bg-glass-hover"
                  >
                    {driftBucket && (
                      <td className="px-2 py-2">
                        {row.linkId && (
                          <input
                            type="checkbox"
                            checked={linkSelected}
                            onChange={(e) => {
                              const s = new Set(selected);
                              if (e.target.checked) s.add(row.linkId!);
                              else s.delete(row.linkId!);
                              setSelected(s);
                            }}
                          />
                        )}
                      </td>
                    )}
                    <td className="px-2 py-2">
                      <Link
                        to="/contacts/$contactId"
                        params={{ contactId: row.contactId }}
                        className="text-foreground hover:underline"
                      >
                        {fullName}
                      </Link>
                    </td>
                    <td className="px-2 py-2 font-mono text-muted-foreground">
                      {row.email}
                    </td>
                    <td className="px-2 py-2 text-muted-foreground">
                      {row.companyId ? (
                        <Link
                          to="/companies/$companyId"
                          params={{ companyId: row.companyId }}
                          className="hover:underline"
                        >
                          {row.companyName ?? "—"}
                        </Link>
                      ) : (
                        "—"
                      )}
                    </td>
                    <td className="px-2 py-2">
                      {row.adsolutContactId ? (
                        <span className="inline-flex items-center gap-1 rounded-full border border-emerald-400/30 bg-emerald-500/10 px-2 py-0.5 text-[10px] text-emerald-300">
                          Linked
                        </span>
                      ) : (
                        <span className="inline-flex items-center gap-1 rounded-full border border-glass-strong bg-glass px-2 py-0.5 text-[10px] text-muted-foreground">
                          Not linked
                        </span>
                      )}
                    </td>
                    <td className="px-2 py-2 text-muted-foreground tabular-nums">
                      {fmtDate(row.contactUpdatedUtc)}
                    </td>
                    <td className="px-2 py-2 text-right">
                      <div className="flex items-center justify-end gap-1">
                        <Button
                          size="sm"
                          variant="ghost"
                          className="h-7 px-2 text-[11px]"
                          onClick={async () => {
                            try {
                              const c = await contactApi.get(row.contactId);
                              setEditContact(c);
                            } catch {
                              toast.error("Could not load contact");
                            }
                          }}
                        >
                          Edit
                        </Button>
                        {linkableLinkBucket && row.linkId && (
                          <Button
                            size="sm"
                            variant="ghost"
                            className="h-7 px-2 text-[11px]"
                            onClick={() => setLinkTarget(row)}
                          >
                            <Link2 className="mr-1 h-3 w-3" />
                            Link
                          </Button>
                        )}
                        {driftBucket && row.linkId && (
                          <Button
                            size="sm"
                            variant="ghost"
                            className="h-7 px-2 text-[11px]"
                            disabled={forcePush.isPending}
                            onClick={() => row.linkId && forcePush.mutate(row.linkId)}
                          >
                            <RefreshCw className="mr-1 h-3 w-3" />
                            Force sync
                          </Button>
                        )}
                        <Button
                          size="sm"
                          variant="ghost"
                          className="h-7 px-2 text-[11px] text-rose-300 hover:text-rose-200"
                          onClick={() => setDeleteTarget(row)}
                          title="Open the contact detail page to delete"
                        >
                          <ExternalLink className="h-3 w-3" />
                        </Button>
                      </div>
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>

          <PageNav page={page} totalPages={totalPages} onPageChange={onPageChange} total={total} />

          <p className="text-[10px] text-muted-foreground/60">
            Header checkbox is intentionally action-driven — bulk-select is
            opt-in per row to avoid an accidental "force-sync all" click.
          </p>
        </div>
      )}

      <ContactFormDialog
        open={!!editContact}
        mode="edit"
        initial={editContact}
        onClose={() => {
          setEditContact(null);
          invalidateAll();
        }}
        onSaved={() => {
          setEditContact(null);
          invalidateAll();
        }}
      />

      {linkTarget && linkTarget.linkId && (
        <LinkContactDialog
          open
          row={linkTarget}
          onClose={() => setLinkTarget(null)}
          onLinked={() => {
            setLinkTarget(null);
            invalidateAll();
          }}
        />
      )}

      {deleteTarget && (
        <Dialog open onOpenChange={(v) => !v && setDeleteTarget(null)}>
          <DialogContent>
            <DialogHeader>
              <DialogTitle>Open contact to delete</DialogTitle>
              <DialogDescription>
                Contact deletion lives on the contact detail page so the
                full link list, audit trail, and ticket history are visible
                first.
              </DialogDescription>
            </DialogHeader>
            <DialogFooter>
              <Button variant="ghost" onClick={() => setDeleteTarget(null)}>
                Cancel
              </Button>
              <Link
                to="/contacts/$contactId"
                params={{ contactId: deleteTarget.contactId }}
                onClick={() => setDeleteTarget(null)}
              >
                <Button>Open contact</Button>
              </Link>
            </DialogFooter>
          </DialogContent>
        </Dialog>
      )}
    </section>
  );
}

// ---- Link dialogs ----------------------------------------------------

type LinkCompanyDialogProps = {
  open: boolean;
  row: AdsolutCoverageCompanyRow;
  onClose: () => void;
  onLinked: () => void;
};

function LinkCompanyDialog({ open, row, onClose, onLinked }: LinkCompanyDialogProps) {
  const [adsolutId, setAdsolutId] = React.useState("");
  const [lastModified, setLastModified] = React.useState("");

  const link = useMutation({
    mutationFn: () =>
      adsolutApi.coverageLinkCompany(
        row.id,
        adsolutId.trim(),
        lastModified.trim() ? lastModified.trim() : null,
      ),
    onSuccess: () => {
      toast.success("Linked to Adsolut");
      onLinked();
    },
    onError: (err) => {
      toast.error(err instanceof ApiError ? `Link failed (${err.status})` : "Link failed");
    },
  });

  const canSubmit = isUuid(adsolutId) && !link.isPending;

  return (
    <Dialog open={open} onOpenChange={(v) => !v && onClose()}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Link {row.name} to Adsolut</DialogTitle>
          <DialogDescription>
            Paste the Adsolut customer UUID. Find it via the Lookup card on
            the Adsolut settings page (search by code or email). The
            lastModified timestamp is optional — leave blank to use server
            time; the next sync tick reconciles either way.
          </DialogDescription>
        </DialogHeader>
        <div className="space-y-3">
          <div className="space-y-1">
            <label className="text-xs text-muted-foreground">Adsolut customer UUID</label>
            <Input
              value={adsolutId}
              onChange={(e) => setAdsolutId(e.target.value)}
              placeholder="c04146fc-48ce-4f59-928a-1aad4d3bbbf9"
              className="font-mono text-[12px]"
            />
            {adsolutId.trim() && !isUuid(adsolutId) && (
              <p className="text-[11px] text-amber-300">Must be a UUID.</p>
            )}
          </div>
          <div className="space-y-1">
            <label className="text-xs text-muted-foreground">
              lastModified (ISO 8601, optional)
            </label>
            <Input
              value={lastModified}
              onChange={(e) => setLastModified(e.target.value)}
              placeholder="2026-05-05T12:34:56Z"
              className="font-mono text-[12px]"
            />
          </div>
        </div>
        <DialogFooter>
          <Button variant="ghost" onClick={onClose}>
            Cancel
          </Button>
          <Button onClick={() => link.mutate()} disabled={!canSubmit}>
            {link.isPending ? "Linking…" : "Link"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

type LinkContactDialogProps = {
  open: boolean;
  row: AdsolutCoverageContactRow;
  onClose: () => void;
  onLinked: () => void;
};

function LinkContactDialog({ open, row, onClose, onLinked }: LinkContactDialogProps) {
  const [adsolutId, setAdsolutId] = React.useState("");
  const [lastModified, setLastModified] = React.useState("");

  const link = useMutation({
    mutationFn: () => {
      if (!row.linkId) throw new Error("missing_link_id");
      return adsolutApi.coverageLinkContact(
        row.linkId,
        adsolutId.trim(),
        lastModified.trim() ? lastModified.trim() : null,
      );
    },
    onSuccess: () => {
      toast.success("Linked contact to Adsolut");
      onLinked();
    },
    onError: (err) => {
      toast.error(err instanceof ApiError ? `Link failed (${err.status})` : "Link failed");
    },
  });

  const canSubmit = isUuid(adsolutId) && !link.isPending;

  return (
    <Dialog open={open} onOpenChange={(v) => !v && onClose()}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>
            Link {row.email} ({row.companyName ?? "no company"}) to Adsolut
          </DialogTitle>
          <DialogDescription>
            Paste the Adsolut customer-contact UUID for this work-relationship.
            One contact-link per Adsolut UUID — three customers under the
            same email = three different UUIDs.
          </DialogDescription>
        </DialogHeader>
        <div className="space-y-3">
          <div className="space-y-1">
            <label className="text-xs text-muted-foreground">
              Adsolut customer-contact UUID
            </label>
            <Input
              value={adsolutId}
              onChange={(e) => setAdsolutId(e.target.value)}
              placeholder="b3f1c0a4-6c13-4d4e-9f3a-7d2b0a1f7c5e"
              className="font-mono text-[12px]"
            />
            {adsolutId.trim() && !isUuid(adsolutId) && (
              <p className="text-[11px] text-amber-300">Must be a UUID.</p>
            )}
          </div>
          <div className="space-y-1">
            <label className="text-xs text-muted-foreground">
              lastModified (ISO 8601, optional)
            </label>
            <Input
              value={lastModified}
              onChange={(e) => setLastModified(e.target.value)}
              placeholder="2026-05-05T12:34:56Z"
              className="font-mono text-[12px]"
            />
          </div>
        </div>
        <DialogFooter>
          <Button variant="ghost" onClick={onClose}>
            Cancel
          </Button>
          <Button onClick={() => link.mutate()} disabled={!canSubmit}>
            {link.isPending ? "Linking…" : "Link"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

// ---- Pagination -----------------------------------------------------

type PageNavProps = {
  page: number;
  totalPages: number;
  total: number;
  onPageChange: (next: number) => void;
};

function PageNav({ page, totalPages, total, onPageChange }: PageNavProps) {
  return (
    <div className="flex items-center justify-between gap-2 px-2 py-1 text-[11px] text-muted-foreground">
      <span className="tabular-nums">
        Page {page} of {totalPages} · {total} rows
      </span>
      <div className="flex items-center gap-1">
        <Button
          size="sm"
          variant="ghost"
          disabled={page <= 1}
          onClick={() => onPageChange(page - 1)}
          className="h-7 text-[11px]"
        >
          Prev
        </Button>
        <Button
          size="sm"
          variant="ghost"
          disabled={page >= totalPages}
          onClick={() => onPageChange(page + 1)}
          className="h-7 text-[11px]"
        >
          Next
        </Button>
      </div>
    </div>
  );
}

function shortId(id: string) {
  return id.length > 8 ? id.slice(0, 8) : id;
}
