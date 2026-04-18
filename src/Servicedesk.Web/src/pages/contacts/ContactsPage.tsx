import * as React from "react";
import { useQuery } from "@tanstack/react-query";
import { Link, useNavigate } from "@tanstack/react-router";
import {
  ArrowUpDown,
  Building2,
  ChevronLeft,
  ChevronRight,
  Search,
  X,
} from "lucide-react";
import {
  contactApi,
  companyApi,
  type ContactBrowseQuery,
  type CompanyPickerItem,
} from "@/lib/ticket-api";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { Switch } from "@/components/ui/switch";
import {
  Popover,
  PopoverContent,
  PopoverTrigger,
} from "@/components/ui/popover";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import { ContactFormDialog } from "@/components/ContactFormDialog";
import { cn } from "@/lib/utils";

type SortKey = NonNullable<ContactBrowseQuery["sort"]>;
type RoleFilter = NonNullable<ContactBrowseQuery["role"]>;

const SORT_LABELS: Record<SortKey, string> = {
  name_asc: "Name (A–Z)",
  email_asc: "Email (A–Z)",
  last_activity_desc: "Latest activity",
};

const ROLE_LABELS: Record<RoleFilter, string> = {
  primary: "Primary link",
  secondary: "Secondary link",
  supplier: "Supplier link",
  none: "No company links",
};

export function ContactsPage() {
  const navigate = useNavigate();
  const [search, setSearch] = React.useState("");
  const [debouncedSearch, setDebouncedSearch] = React.useState("");
  const [companyFilter, setCompanyFilter] = React.useState<CompanyPickerItem | null>(null);
  const [roleFilter, setRoleFilter] = React.useState<RoleFilter | "any">("any");
  const [includeInactive, setIncludeInactive] = React.useState(false);
  const [sort, setSort] = React.useState<SortKey>("name_asc");
  const [page, setPage] = React.useState(1);
  const [creating, setCreating] = React.useState(false);

  // Debounce the search input so we don't re-query on every keystroke.
  React.useEffect(() => {
    const timer = setTimeout(() => {
      setDebouncedSearch(search);
      setPage(1);
    }, 250);
    return () => clearTimeout(timer);
  }, [search]);

  // Filter changes reset pagination — otherwise a user on page 4 suddenly
  // sees an empty screen when they narrow the filter.
  React.useEffect(() => {
    setPage(1);
  }, [companyFilter?.id, roleFilter, includeInactive, sort]);

  const query: ContactBrowseQuery = {
    search: debouncedSearch || undefined,
    companyId: companyFilter?.id,
    role: roleFilter === "any" ? undefined : roleFilter,
    includeInactive,
    sort,
    page,
  };

  const { data, isLoading, isFetching } = useQuery({
    queryKey: ["contacts", "browse", query],
    queryFn: () => contactApi.browse(query),
    placeholderData: (prev) => prev,
  });

  const total = data?.total ?? 0;
  const pageSize = data?.pageSize ?? 25;
  const totalPages = Math.max(1, Math.ceil(total / pageSize));
  const items = data?.items ?? [];

  return (
    <div className="flex min-h-[calc(100vh-8rem)] w-full flex-col gap-6">
      <header className="flex items-start justify-between gap-4">
        <div className="space-y-1">
          <h1 className="text-display-md font-semibold text-foreground">Contacts</h1>
          <p className="text-sm text-muted-foreground">
            Every contact across all companies — with their primary link, extra
            connections and most recent ticket activity. Use the filters to
            narrow down by company, role or status.
          </p>
        </div>
        <Badge className="border border-white/10 bg-white/[0.05] text-xs font-normal text-muted-foreground">
          Admin only
        </Badge>
      </header>

      <div className="flex flex-wrap items-center gap-3">
        <div className="relative flex-1 min-w-[240px] max-w-md">
          <Search className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground" />
          <Input
            value={search}
            onChange={(e) => setSearch(e.target.value)}
            placeholder="Search by name, email or phone…"
            className="pl-9"
          />
        </div>

        <CompanyFilterPicker value={companyFilter} onChange={setCompanyFilter} />

        <Select value={roleFilter} onValueChange={(v) => setRoleFilter(v as RoleFilter | "any")}>
          <SelectTrigger className="w-[180px]">
            <SelectValue placeholder="All roles" />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value="any">All roles</SelectItem>
            {(Object.keys(ROLE_LABELS) as RoleFilter[]).map((r) => (
              <SelectItem key={r} value={r}>
                {ROLE_LABELS[r]}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>

        <Select value={sort} onValueChange={(v) => setSort(v as SortKey)}>
          <SelectTrigger className="w-[180px]">
            <ArrowUpDown className="h-3.5 w-3.5 text-muted-foreground" />
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            {(Object.keys(SORT_LABELS) as SortKey[]).map((s) => (
              <SelectItem key={s} value={s}>
                {SORT_LABELS[s]}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>

        <label className="flex items-center gap-2 text-xs text-muted-foreground">
          <Switch checked={includeInactive} onCheckedChange={setIncludeInactive} />
          Show inactive
        </label>

        <div className="ml-auto">
          <Button onClick={() => setCreating(true)}>+ New contact</Button>
        </div>
      </div>

      {isLoading ? (
        <div className="space-y-2">
          {[...Array(6)].map((_, i) => (
            <Skeleton key={i} className="h-12 w-full" />
          ))}
        </div>
      ) : (
        <section
          className={cn(
            "glass-card overflow-hidden transition-opacity",
            isFetching && "opacity-70",
          )}
        >
          <table className="w-full text-left text-sm">
            <thead className="text-xs uppercase tracking-wide text-muted-foreground [&_th]:border-b [&_th]:border-white/10">
              <tr>
                <th className="px-4 py-3 font-medium">Name</th>
                <th className="px-4 py-3 font-medium">Email</th>
                <th className="px-4 py-3 font-medium">Phone</th>
                <th className="px-4 py-3 font-medium">Primary company</th>
                <th className="px-4 py-3 font-medium">Extra links</th>
                <th className="px-4 py-3 font-medium">Last activity</th>
                <th className="px-4 py-3 font-medium">Status</th>
              </tr>
            </thead>
            <tbody>
              {items.map((c) => {
                const fullName = [c.firstName, c.lastName].filter(Boolean).join(" ").trim();
                return (
                  <tr key={c.id} className="border-b border-white/5 hover:bg-white/[0.03]">
                    <td className="px-4 py-3 text-foreground">
                      <Link
                        to="/contacts/$contactId"
                        params={{ contactId: c.id }}
                        className="hover:text-primary"
                      >
                        {fullName || <span className="text-muted-foreground">—</span>}
                      </Link>
                      {c.jobTitle ? (
                        <div className="text-xs text-muted-foreground">{c.jobTitle}</div>
                      ) : null}
                    </td>
                    <td className="px-4 py-3 text-muted-foreground">{c.email}</td>
                    <td className="px-4 py-3 text-muted-foreground">
                      {c.phone || <span className="text-xs">—</span>}
                    </td>
                    <td className="px-4 py-3">
                      {c.primaryCompanyId && c.primaryCompanyName ? (
                        <Link
                          to="/companies/$companyId"
                          params={{ companyId: c.primaryCompanyId }}
                          className="inline-flex items-center gap-1.5 text-foreground hover:text-primary"
                        >
                          <Building2 className="h-3.5 w-3.5 text-muted-foreground" />
                          <span>{c.primaryCompanyShortName || c.primaryCompanyName}</span>
                          {c.primaryCompanyCode && (
                            <span className="font-mono text-[10px] text-muted-foreground">
                              {c.primaryCompanyCode}
                            </span>
                          )}
                        </Link>
                      ) : (
                        <span className="text-xs text-muted-foreground">—</span>
                      )}
                    </td>
                    <td className="px-4 py-3">
                      {c.extraLinkCount > 0 ? (
                        <Badge className="border border-white/10 bg-white/[0.05] text-[10px] font-normal text-muted-foreground">
                          +{c.extraLinkCount}
                        </Badge>
                      ) : (
                        <span className="text-xs text-muted-foreground">—</span>
                      )}
                    </td>
                    <td className="px-4 py-3 text-xs text-muted-foreground">
                      {c.lastTicketUpdatedUtc
                        ? new Date(c.lastTicketUpdatedUtc).toLocaleDateString()
                        : "—"}
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
                  </tr>
                );
              })}
              {items.length === 0 && (
                <tr>
                  <td colSpan={7} className="p-8 text-center text-sm text-muted-foreground">
                    No contacts match these filters.
                  </td>
                </tr>
              )}
            </tbody>
          </table>
        </section>
      )}

      {total > 0 && (
        <footer className="flex items-center justify-between text-xs text-muted-foreground">
          <div>
            {(page - 1) * pageSize + 1}–{Math.min(page * pageSize, total)} of {total}
          </div>
          <div className="flex items-center gap-1">
            <Button
              variant="ghost"
              size="sm"
              onClick={() => setPage((p) => Math.max(1, p - 1))}
              disabled={page <= 1}
            >
              <ChevronLeft className="h-3.5 w-3.5" />
              Prev
            </Button>
            <span className="px-2 tabular-nums">
              Page {page} / {totalPages}
            </span>
            <Button
              variant="ghost"
              size="sm"
              onClick={() => setPage((p) => Math.min(totalPages, p + 1))}
              disabled={page >= totalPages}
            >
              Next
              <ChevronRight className="h-3.5 w-3.5" />
            </Button>
          </div>
        </footer>
      )}

      <ContactFormDialog
        open={creating}
        mode="create"
        onClose={() => setCreating(false)}
        onSaved={(saved) => {
          setCreating(false);
          // Land the user on the detail page so they can link companies,
          // review the contact they just created and get immediate feedback.
          navigate({ to: "/contacts/$contactId", params: { contactId: saved.id } });
        }}
      />
    </div>
  );
}

/// Inline company filter for the overview. Uses the agent-scoped
/// /api/companies/picker endpoint (active-only, capped at 20) so the filter
/// works for Agent principals — /api/companies is admin-only.
function CompanyFilterPicker({
  value,
  onChange,
}: {
  value: CompanyPickerItem | null;
  onChange: (v: CompanyPickerItem | null) => void;
}) {
  const [open, setOpen] = React.useState(false);
  const [search, setSearch] = React.useState("");
  const [debouncedSearch, setDebouncedSearch] = React.useState("");

  React.useEffect(() => {
    const timer = setTimeout(() => setDebouncedSearch(search), 200);
    return () => clearTimeout(timer);
  }, [search]);

  const { data } = useQuery({
    queryKey: ["companies", "picker", debouncedSearch],
    queryFn: () => companyApi.picker(debouncedSearch || undefined),
    placeholderData: (prev) => prev,
    enabled: open,
  });

  return (
    <Popover open={open} onOpenChange={setOpen}>
      <PopoverTrigger asChild>
        <button
          type="button"
          className={cn(
            "h-9 px-3 rounded-[var(--radius)] border border-white/10 bg-white/[0.04] text-sm",
            "hover:bg-white/[0.07] transition-colors text-left min-w-[180px]",
            "flex items-center justify-between gap-2",
          )}
        >
          <span className="flex items-center gap-1.5 truncate">
            <Building2 className="h-3.5 w-3.5 text-muted-foreground" />
            {value ? (
              <span className="truncate">{value.shortName || value.name}</span>
            ) : (
              <span className="text-muted-foreground">All companies</span>
            )}
          </span>
          {value && (
            <span
              role="button"
              tabIndex={0}
              className="ml-1 text-muted-foreground hover:text-foreground"
              onClick={(e) => {
                e.stopPropagation();
                onChange(null);
              }}
              onKeyDown={(e) => {
                if (e.key === "Enter" || e.key === " ") {
                  e.stopPropagation();
                  onChange(null);
                }
              }}
            >
              <X className="h-3.5 w-3.5" />
            </span>
          )}
        </button>
      </PopoverTrigger>
      <PopoverContent className="w-[280px] p-2" align="start">
        <Input
          autoFocus
          placeholder="Search companies…"
          value={search}
          onChange={(e) => setSearch(e.target.value)}
          className="mb-2"
        />
        <div className="max-h-[260px] overflow-auto">
          {(data ?? []).length === 0 ? (
            <div className="px-2 py-3 text-xs text-muted-foreground">No matches.</div>
          ) : (
            (data ?? []).map((c) => (
              <button
                key={c.id}
                type="button"
                onClick={() => {
                  onChange(c);
                  setOpen(false);
                }}
                className="flex w-full items-center justify-between gap-2 rounded-md px-2 py-1.5 text-left text-sm hover:bg-white/[0.05]"
              >
                <span className="truncate">{c.shortName || c.name}</span>
                <span className="font-mono text-[10px] text-muted-foreground">{c.code}</span>
              </button>
            ))
          )}
        </div>
      </PopoverContent>
    </Popover>
  );
}
