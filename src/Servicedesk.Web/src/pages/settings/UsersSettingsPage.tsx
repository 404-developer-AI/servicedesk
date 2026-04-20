import { useState, useMemo, useRef, useEffect } from "react";
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import {
  UserCog,
  ShieldCheck,
  ShieldOff,
  MoreHorizontal,
  Trash2,
  UserPlus,
  ArrowUpCircle,
  CheckCircle2,
  Circle,
  Search,
  Loader2,
  KeyRound,
  Cloud,
  Eye,
  EyeOff,
  Mail,
} from "lucide-react";
import { adminUserApi, type UserAdminRow, type M365PickerUser } from "@/lib/ticket-api";
import { authApi } from "@/lib/api";
import { useAuth } from "@/auth/authStore";
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
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { cn } from "@/lib/utils";

type Role = "Agent" | "Admin";

// ---- Page --------------------------------------------------------------

export function UsersSettingsPage() {
  const { user: current } = useAuth();
  const qc = useQueryClient();

  const { data: users, isLoading } = useQuery({
    queryKey: ["admin", "users"],
    queryFn: () => adminUserApi.list(),
  });

  // Gate the "Add from M365" + "Upgrade to M365" flows on the same
  // feature-flag the server enforces. Without this, clicking "Add" with
  // M365 off would open a dialog that only shows a 409 error. Cached —
  // same /api/auth/config the login page reads.
  const { data: authConfig } = useQuery({
    queryKey: ["auth", "config"],
    queryFn: () => authApi.config(),
    staleTime: 60_000,
  });
  const m365Enabled = authConfig?.microsoftEnabled === true;

  const [addOpen, setAddOpen] = useState(false);
  const [addLocalOpen, setAddLocalOpen] = useState(false);
  const [upgradeTarget, setUpgradeTarget] = useState<UserAdminRow | null>(null);
  const [deleteTarget, setDeleteTarget] = useState<UserAdminRow | null>(null);

  const [search, setSearch] = useState("");
  const filtered = useMemo(() => {
    if (!users) return [];
    const q = search.trim().toLowerCase();
    if (!q) return users;
    return users.filter(
      (u) =>
        u.email.toLowerCase().includes(q) ||
        u.role.toLowerCase().includes(q) ||
        u.authMode.toLowerCase().includes(q),
    );
  }, [users, search]);

  function invalidate() {
    qc.invalidateQueries({ queryKey: ["admin", "users"] });
  }

  return (
    <div className="flex flex-col gap-6">
      <header className="flex items-start justify-between gap-4">
        <div className="flex items-center gap-3">
          <div className="flex h-9 w-9 items-center justify-center rounded-lg bg-primary/20 border border-primary/30">
            <UserCog className="h-4 w-4 text-primary" />
          </div>
          <div>
            <h1 className="text-display-md font-semibold leading-tight text-foreground">
              Users
            </h1>
            {!isLoading && users && (
              <p className="text-xs text-muted-foreground">
                {users.length} user{users.length !== 1 ? "s" : ""} ·{" "}
                {users.filter((u) => u.authMode === "Microsoft").length} via M365
              </p>
            )}
          </div>
        </div>
        <div className="flex items-center gap-2">
          <Badge className="border border-white/10 bg-white/[0.05] text-xs font-normal text-muted-foreground">
            Admin only
          </Badge>
          <Button
            onClick={() => setAddLocalOpen(true)}
            size="sm"
            variant="secondary"
            className="gap-1.5"
          >
            <KeyRound className="h-4 w-4" />
            Add local
          </Button>
          <Button
            onClick={() => setAddOpen(true)}
            size="sm"
            className="gap-1.5"
            disabled={!m365Enabled}
            title={
              m365Enabled
                ? undefined
                : "Enable Microsoft 365 sign-in first under Settings → Mail → Microsoft Graph."
            }
          >
            <Cloud className="h-4 w-4" />
            Add from M365
          </Button>
        </div>
      </header>

      {!m365Enabled && (
        <div className="rounded-lg border border-amber-500/30 bg-amber-500/[0.08] px-4 py-3 text-xs text-amber-200">
          <p className="font-medium mb-0.5">Microsoft 365 sign-in is off</p>
          <p className="opacity-90">
            Add / Upgrade-to-M365 actions are disabled until you turn on
            <span className="font-mono"> Auth.Microsoft.Enabled </span>
            under Settings → Mail → Microsoft Graph. You can still manage local
            accounts from this page.
          </p>
        </div>
      )}

      <div className="relative">
        <Search className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground" />
        <Input
          placeholder="Search email, role, auth mode…"
          value={search}
          onChange={(e) => setSearch(e.target.value)}
          className="pl-9"
        />
      </div>

      {isLoading ? (
        <div className="space-y-2">
          {Array.from({ length: 4 }).map((_, i) => (
            <Skeleton key={i} className="h-[72px] w-full rounded-lg" />
          ))}
        </div>
      ) : !users || users.length === 0 ? (
        <EmptyState
          onAddLocal={() => setAddLocalOpen(true)}
          onAddM365={() => setAddOpen(true)}
          m365Enabled={m365Enabled}
        />
      ) : (
        <div className="space-y-2">
          {filtered.map((u) => (
            <UserRow
              key={u.id}
              user={u}
              isSelf={current?.id === u.id}
              m365Enabled={m365Enabled}
              onUpgrade={() => setUpgradeTarget(u)}
              onDelete={() => setDeleteTarget(u)}
              onChanged={invalidate}
            />
          ))}
          {filtered.length === 0 && (
            <div className="rounded-lg border border-dashed border-white/[0.08] bg-white/[0.02] px-6 py-8 text-center text-sm text-muted-foreground">
              No users match “{search}”.
            </div>
          )}
        </div>
      )}

      <AddLocalDialog
        open={addLocalOpen}
        onOpenChange={setAddLocalOpen}
        onAdded={invalidate}
      />

      <AddFromM365Dialog
        open={addOpen}
        onOpenChange={setAddOpen}
        onAdded={invalidate}
      />

      <UpgradeToM365Dialog
        target={upgradeTarget}
        onOpenChange={(open) => !open && setUpgradeTarget(null)}
        onUpgraded={() => {
          invalidate();
          setUpgradeTarget(null);
        }}
      />

      <DeleteConfirmDialog
        target={deleteTarget}
        onOpenChange={(open) => !open && setDeleteTarget(null)}
        onDeleted={() => {
          invalidate();
          setDeleteTarget(null);
        }}
      />
    </div>
  );
}

// ---- Empty state -------------------------------------------------------

function EmptyState({
  onAddLocal,
  onAddM365,
  m365Enabled,
}: {
  onAddLocal: () => void;
  onAddM365: () => void;
  m365Enabled: boolean;
}) {
  return (
    <div className="rounded-lg border border-white/[0.06] bg-white/[0.02] px-6 py-10 flex flex-col items-center justify-center gap-3 text-center">
      <div className="flex h-10 w-10 items-center justify-center rounded-xl bg-primary/10 border border-primary/20">
        <UserCog className="h-5 w-5 text-primary/60" />
      </div>
      <div>
        <p className="text-sm font-medium text-foreground">No users yet</p>
        <p className="text-xs text-muted-foreground mt-1">
          Add an agent or admin to get started.
        </p>
      </div>
      <div className="mt-2 flex gap-2">
        <Button onClick={onAddLocal} size="sm" variant="outline" className="gap-1.5">
          <KeyRound className="h-4 w-4" />
          Add local
        </Button>
        <Button
          onClick={onAddM365}
          size="sm"
          variant="outline"
          className="gap-1.5"
          disabled={!m365Enabled}
        >
          <Cloud className="h-4 w-4" />
          Add from M365
        </Button>
      </div>
    </div>
  );
}

// ---- User row ----------------------------------------------------------

function UserRow({
  user,
  isSelf,
  m365Enabled,
  onUpgrade,
  onDelete,
  onChanged,
}: {
  user: UserAdminRow;
  isSelf: boolean;
  m365Enabled: boolean;
  onUpgrade: () => void;
  onDelete: () => void;
  onChanged: () => void;
}) {
  const [mutationPending, setMutationPending] = useState(false);

  async function runAction<T>(fn: () => Promise<T>, successMessage: string) {
    setMutationPending(true);
    try {
      await fn();
      toast.success(successMessage);
      onChanged();
    } catch (err) {
      const message =
        err instanceof Error ? err.message : "Something went wrong.";
      toast.error(message);
    } finally {
      setMutationPending(false);
    }
  }

  const isMicrosoft = user.authMode === "Microsoft";

  return (
    <div
      className={cn(
        "group flex items-center gap-4 rounded-lg border border-white/[0.06] bg-white/[0.02] px-4 py-3 transition-colors",
        !user.isActive && "opacity-60",
      )}
    >
      <div className="min-w-0 flex-1">
        <div className="flex items-center gap-2">
          <p className="truncate text-sm font-medium text-foreground">
            {user.email}
          </p>
          {isSelf && (
            <Badge className="border border-primary/30 bg-primary/15 text-[10px] font-medium text-primary px-1.5 py-0">
              You
            </Badge>
          )}
          {!user.isActive && (
            <Badge className="border border-white/10 bg-white/[0.04] text-[10px] font-medium text-muted-foreground px-1.5 py-0">
              Inactive
            </Badge>
          )}
        </div>
        <div className="mt-1 flex items-center gap-3 text-xs text-muted-foreground">
          <span className="inline-flex items-center gap-1">
            {isMicrosoft ? (
              <Cloud className="h-3 w-3" />
            ) : (
              <KeyRound className="h-3 w-3" />
            )}
            {isMicrosoft ? "Microsoft 365" : "Local account"}
          </span>
          {user.role === "Admin" ? (
            <span className="inline-flex items-center gap-1 text-amber-400/80">
              <ShieldCheck className="h-3 w-3" />
              Admin
            </span>
          ) : (
            <span className="inline-flex items-center gap-1">
              <ShieldOff className="h-3 w-3" />
              Agent
            </span>
          )}
          {!isMicrosoft && user.twoFactorEnabled && (
            <span className="text-emerald-400/80">2FA on</span>
          )}
          {user.lastLoginUtc && (
            <span>last login {formatRelative(user.lastLoginUtc)}</span>
          )}
        </div>
      </div>

      <RoleChip
        role={user.role}
        disabled={isSelf || mutationPending}
        onChange={(nextRole) =>
          runAction(
            () => adminUserApi.updateRole(user.id, nextRole),
            `Role changed to ${nextRole}`,
          )
        }
      />

      <ActiveToggle
        active={user.isActive}
        disabled={isSelf || mutationPending}
        onToggle={() =>
          runAction(
            () =>
              user.isActive
                ? adminUserApi.deactivate(user.id)
                : adminUserApi.activate(user.id),
            user.isActive ? "User deactivated" : "User activated",
          )
        }
      />

      <DropdownMenu>
        <DropdownMenuTrigger asChild>
          <button
            type="button"
            className="h-8 w-8 shrink-0 inline-flex items-center justify-center rounded-lg text-muted-foreground transition-colors hover:bg-white/[0.04] hover:text-foreground"
            aria-label="Row actions"
          >
            <MoreHorizontal className="h-4 w-4" />
          </button>
        </DropdownMenuTrigger>
        <DropdownMenuContent align="end" className="w-52">
          {!isMicrosoft && (
            <DropdownMenuItem
              disabled={isSelf || mutationPending || !m365Enabled}
              onSelect={(e) => {
                e.preventDefault();
                if (!m365Enabled) return;
                onUpgrade();
              }}
              title={
                !m365Enabled
                  ? "Enable M365 sign-in first"
                  : isSelf
                  ? "You can't upgrade your own account"
                  : undefined
              }
            >
              <ArrowUpCircle className="mr-2 h-4 w-4" />
              Upgrade to M365…
            </DropdownMenuItem>
          )}
          <DropdownMenuSeparator />
          <DropdownMenuItem
            disabled={isSelf || mutationPending}
            onSelect={(e) => {
              e.preventDefault();
              onDelete();
            }}
            className="text-red-400 focus:text-red-400"
          >
            <Trash2 className="mr-2 h-4 w-4" />
            Delete user…
          </DropdownMenuItem>
        </DropdownMenuContent>
      </DropdownMenu>
    </div>
  );
}

// ---- Role chip ---------------------------------------------------------

function RoleChip({
  role,
  disabled,
  onChange,
}: {
  role: UserAdminRow["role"];
  disabled: boolean;
  onChange: (role: Role) => void;
}) {
  if (role === "Customer") {
    return (
      <span className="shrink-0 rounded-full border border-white/10 bg-white/[0.03] px-2.5 py-0.5 text-[11px] text-muted-foreground">
        Customer
      </span>
    );
  }

  const nextRole: Role = role === "Admin" ? "Agent" : "Admin";

  return (
    <button
      type="button"
      disabled={disabled}
      onClick={() => onChange(nextRole)}
      title={disabled ? "You can't change your own role" : `Switch to ${nextRole}`}
      className={cn(
        "shrink-0 inline-flex items-center gap-1.5 rounded-full border px-2.5 py-0.5 text-[11px] font-medium transition-colors",
        "disabled:opacity-50 disabled:cursor-not-allowed",
        role === "Admin"
          ? "border-amber-400/40 bg-amber-400/15 text-amber-300 hover:bg-amber-400/20"
          : "border-white/[0.08] bg-white/[0.02] text-foreground hover:bg-white/[0.05]",
      )}
    >
      {role}
    </button>
  );
}

// ---- Active toggle -----------------------------------------------------

function ActiveToggle({
  active,
  disabled,
  onToggle,
}: {
  active: boolean;
  disabled: boolean;
  onToggle: () => void;
}) {
  return (
    <button
      type="button"
      disabled={disabled}
      onClick={onToggle}
      title={
        disabled
          ? "You can't deactivate your own account"
          : active
          ? "Deactivate"
          : "Activate"
      }
      className={cn(
        "shrink-0 inline-flex items-center gap-1.5 rounded-full border px-2.5 py-0.5 text-[11px] font-medium transition-colors",
        "disabled:opacity-50 disabled:cursor-not-allowed",
        active
          ? "border-emerald-500/40 bg-emerald-500/15 text-emerald-400 hover:bg-emerald-500/20"
          : "border-white/[0.08] bg-white/[0.02] text-muted-foreground hover:bg-white/[0.05]",
      )}
    >
      {active ? (
        <CheckCircle2 className="h-3 w-3" />
      ) : (
        <Circle className="h-3 w-3" />
      )}
      {active ? "Active" : "Inactive"}
    </button>
  );
}

// ---- Add-from-M365 dialog ---------------------------------------------

function AddFromM365Dialog({
  open,
  onOpenChange,
  onAdded,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  onAdded: () => void;
}) {
  const [query, setQuery] = useState("");
  const [selected, setSelected] = useState<M365PickerUser | null>(null);
  const [role, setRole] = useState<Role>("Agent");

  useEffect(() => {
    if (!open) {
      // Reset on close so the next open starts fresh.
      setQuery("");
      setSelected(null);
      setRole("Agent");
    }
  }, [open]);

  const debouncedQuery = useDebounced(query, 250);
  const { data: results, isFetching, error: searchError } = useQuery({
    queryKey: ["admin", "m365-search", debouncedQuery],
    queryFn: () => adminUserApi.searchM365(debouncedQuery),
    enabled: open,
    staleTime: 30_000,
  });

  const add = useMutation({
    mutationFn: () => {
      if (!selected) throw new Error("Pick a user first");
      return adminUserApi.addFromM365(selected.oid, role);
    },
    onSuccess: () => {
      toast.success(`${selected?.displayName ?? selected?.mail} added as ${role}`);
      onAdded();
      onOpenChange(false);
    },
    onError: (err) => {
      toast.error(err instanceof Error ? err.message : "Failed to add user");
    },
  });

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-w-lg">
        <DialogHeader>
          <DialogTitle>Add user from Microsoft 365</DialogTitle>
          <DialogDescription>
            Pick an Azure AD user. The server verifies the object-id before
            creating the row; the user can sign in via “Sign in with
            Microsoft” as soon as this completes.
          </DialogDescription>
        </DialogHeader>

        <div className="space-y-3">
          <div className="relative">
            <Search className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground" />
            <Input
              placeholder="Search name, email or UPN…"
              value={query}
              onChange={(e) => setQuery(e.target.value)}
              className="pl-9"
              autoFocus
            />
            {isFetching && (
              <Loader2 className="pointer-events-none absolute right-3 top-1/2 h-4 w-4 -translate-y-1/2 animate-spin text-muted-foreground" />
            )}
          </div>

          {searchError ? (
            <div className="rounded-lg border border-amber-500/30 bg-amber-500/10 px-3 py-2 text-xs text-amber-300">
              {searchError instanceof Error
                ? searchError.message
                : "Could not reach Microsoft Graph."}
            </div>
          ) : (
            <div className="max-h-[300px] overflow-y-auto rounded-lg border border-white/[0.06] bg-white/[0.02]">
              {(!results || results.length === 0) && !isFetching && (
                <div className="px-4 py-6 text-center text-xs text-muted-foreground">
                  {query ? "No matches." : "Type to search."}
                </div>
              )}
              {results?.map((u) => {
                const isPicked = selected?.oid === u.oid;
                return (
                  <button
                    key={u.oid}
                    type="button"
                    onClick={() => setSelected(u)}
                    className={cn(
                      "flex w-full items-center gap-3 px-3 py-2 text-left transition-colors hover:bg-white/[0.04]",
                      isPicked && "bg-primary/10",
                    )}
                  >
                    <div className="min-w-0 flex-1">
                      <p className="truncate text-sm font-medium text-foreground">
                        {u.displayName ?? u.mail ?? u.userPrincipalName ?? u.oid}
                      </p>
                      <p className="truncate text-xs text-muted-foreground">
                        {u.mail ?? u.userPrincipalName ?? "—"}
                      </p>
                    </div>
                    {!u.accountEnabled && (
                      <Badge className="border border-red-400/40 bg-red-400/10 text-[10px] text-red-300">
                        Disabled
                      </Badge>
                    )}
                    {isPicked && (
                      <CheckCircle2 className="h-4 w-4 shrink-0 text-primary" />
                    )}
                  </button>
                );
              })}
            </div>
          )}

          {selected && (
            <div className="rounded-lg border border-primary/30 bg-primary/5 p-3">
              <p className="text-xs uppercase tracking-wide text-muted-foreground mb-2">
                Role
              </p>
              <div className="flex gap-2">
                {(["Agent", "Admin"] as const).map((r) => (
                  <button
                    key={r}
                    type="button"
                    onClick={() => setRole(r)}
                    className={cn(
                      "flex-1 rounded-md border px-3 py-2 text-sm font-medium transition-colors",
                      role === r
                        ? "border-primary/50 bg-primary/15 text-primary"
                        : "border-white/[0.08] bg-white/[0.02] text-muted-foreground hover:bg-white/[0.04]",
                    )}
                  >
                    {r}
                  </button>
                ))}
              </div>
            </div>
          )}
        </div>

        <DialogFooter>
          <Button variant="ghost" onClick={() => onOpenChange(false)}>
            Cancel
          </Button>
          <Button
            onClick={() => add.mutate()}
            disabled={!selected || add.isPending}
          >
            {add.isPending ? (
              <Loader2 className="mr-2 h-4 w-4 animate-spin" />
            ) : (
              <UserPlus className="mr-2 h-4 w-4" />
            )}
            Add user
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

// ---- Upgrade-to-M365 dialog -------------------------------------------

function UpgradeToM365Dialog({
  target,
  onOpenChange,
  onUpgraded,
}: {
  target: UserAdminRow | null;
  onOpenChange: (open: boolean) => void;
  onUpgraded: () => void;
}) {
  const [query, setQuery] = useState("");
  const [selected, setSelected] = useState<M365PickerUser | null>(null);

  // Auto-suggest search using the target's email so the correct user is
  // typically the first hit. The admin can still refine the query.
  useEffect(() => {
    if (target) {
      setQuery(target.email);
      setSelected(null);
    } else {
      setQuery("");
      setSelected(null);
    }
  }, [target]);

  const debouncedQuery = useDebounced(query, 250);
  const { data: results, isFetching } = useQuery({
    queryKey: ["admin", "m365-search", debouncedQuery],
    queryFn: () => adminUserApi.searchM365(debouncedQuery),
    enabled: target !== null,
    staleTime: 30_000,
  });

  const upgrade = useMutation({
    mutationFn: () => {
      if (!target || !selected) throw new Error("Pick a user");
      return adminUserApi.upgradeToM365(target.id, selected.oid);
    },
    onSuccess: () => {
      toast.success(`${target?.email} is now a Microsoft account`);
      onUpgraded();
    },
    onError: (err) => {
      toast.error(err instanceof Error ? err.message : "Upgrade failed");
    },
  });

  return (
    <Dialog open={target !== null} onOpenChange={onOpenChange}>
      <DialogContent className="max-w-lg">
        <DialogHeader>
          <DialogTitle>Upgrade to Microsoft 365 login</DialogTitle>
          <DialogDescription>
            Links the local <span className="font-medium">{target?.email}</span>{" "}
            account to an Azure AD user. From then on the user signs in via
            Microsoft only.
          </DialogDescription>
        </DialogHeader>

        <div className="rounded-lg border border-amber-500/30 bg-amber-500/10 p-3 text-xs text-amber-200">
          <p className="font-medium mb-1">This action removes local credentials.</p>
          <p>
            The password and any TOTP / recovery codes are deleted in the same
            transaction. Active sessions are revoked. The user must sign in
            again via Microsoft.
          </p>
        </div>

        <div className="space-y-3">
          <div className="relative">
            <Search className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground" />
            <Input
              placeholder="Search name, email or UPN…"
              value={query}
              onChange={(e) => setQuery(e.target.value)}
              className="pl-9"
            />
            {isFetching && (
              <Loader2 className="pointer-events-none absolute right-3 top-1/2 h-4 w-4 -translate-y-1/2 animate-spin text-muted-foreground" />
            )}
          </div>

          <div className="max-h-[260px] overflow-y-auto rounded-lg border border-white/[0.06] bg-white/[0.02]">
            {(!results || results.length === 0) && !isFetching && (
              <div className="px-4 py-6 text-center text-xs text-muted-foreground">
                No matches.
              </div>
            )}
            {results?.map((u) => {
              const isPicked = selected?.oid === u.oid;
              return (
                <button
                  key={u.oid}
                  type="button"
                  onClick={() => setSelected(u)}
                  className={cn(
                    "flex w-full items-center gap-3 px-3 py-2 text-left transition-colors hover:bg-white/[0.04]",
                    isPicked && "bg-primary/10",
                  )}
                >
                  <div className="min-w-0 flex-1">
                    <p className="truncate text-sm font-medium text-foreground">
                      {u.displayName ?? u.mail ?? u.userPrincipalName ?? u.oid}
                    </p>
                    <p className="truncate text-xs text-muted-foreground">
                      {u.mail ?? u.userPrincipalName ?? "—"}
                    </p>
                  </div>
                  {!u.accountEnabled && (
                    <Badge className="border border-red-400/40 bg-red-400/10 text-[10px] text-red-300">
                      Disabled
                    </Badge>
                  )}
                  {isPicked && (
                    <CheckCircle2 className="h-4 w-4 shrink-0 text-primary" />
                  )}
                </button>
              );
            })}
          </div>
        </div>

        <DialogFooter>
          <Button variant="ghost" onClick={() => onOpenChange(false)}>
            Cancel
          </Button>
          <Button
            onClick={() => upgrade.mutate()}
            disabled={!selected || upgrade.isPending}
          >
            {upgrade.isPending ? (
              <Loader2 className="mr-2 h-4 w-4 animate-spin" />
            ) : (
              <ArrowUpCircle className="mr-2 h-4 w-4" />
            )}
            Upgrade
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

// ---- Delete confirm ----------------------------------------------------

function DeleteConfirmDialog({
  target,
  onOpenChange,
  onDeleted,
}: {
  target: UserAdminRow | null;
  onOpenChange: (open: boolean) => void;
  onDeleted: () => void;
}) {
  const remove = useMutation({
    mutationFn: () => {
      if (!target) throw new Error("No target");
      return adminUserApi.remove(target.id);
    },
    onSuccess: () => {
      toast.success(`${target?.email} deleted`);
      onDeleted();
    },
    onError: (err) => {
      toast.error(err instanceof Error ? err.message : "Delete failed");
    },
  });

  return (
    <Dialog open={target !== null} onOpenChange={onOpenChange}>
      <DialogContent className="max-w-md">
        <DialogHeader>
          <DialogTitle>Delete user?</DialogTitle>
          <DialogDescription>
            Permanently removes{" "}
            <span className="font-medium">{target?.email}</span>. This cannot be
            undone. If the user has ticket or timeline activity the server will
            block the delete; deactivate them instead.
          </DialogDescription>
        </DialogHeader>
        <DialogFooter>
          <Button variant="ghost" onClick={() => onOpenChange(false)}>
            Cancel
          </Button>
          <Button
            variant="destructive"
            onClick={() => remove.mutate()}
            disabled={remove.isPending}
          >
            {remove.isPending ? (
              <Loader2 className="mr-2 h-4 w-4 animate-spin" />
            ) : (
              <Trash2 className="mr-2 h-4 w-4" />
            )}
            Delete user
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

// ---- Add local dialog --------------------------------------------------

const MIN_PASSWORD_LENGTH = 12;

function AddLocalDialog({
  open,
  onOpenChange,
  onAdded,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  onAdded: () => void;
}) {
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [role, setRole] = useState<Role>("Agent");
  const [showPw, setShowPw] = useState(false);

  useEffect(() => {
    if (!open) {
      setEmail("");
      setPassword("");
      setRole("Agent");
      setShowPw(false);
    }
  }, [open]);

  const emailValid = email.includes("@") && email.trim().length > 2;
  const pwValid = password.length >= MIN_PASSWORD_LENGTH;
  const formValid = emailValid && pwValid;

  const add = useMutation({
    mutationFn: () => adminUserApi.addLocal(email.trim(), password, role),
    onSuccess: (row) => {
      toast.success(`${row.email} added as ${row.role}`);
      onAdded();
      onOpenChange(false);
    },
    onError: (err) => {
      toast.error(err instanceof Error ? err.message : "Failed to add user");
    },
  });

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-w-md">
        <DialogHeader>
          <DialogTitle>Add local user</DialogTitle>
          <DialogDescription>
            Creates an account with an email / password login. The user can
            enrol TOTP themselves from Profile → Two-factor once they sign in.
          </DialogDescription>
        </DialogHeader>

        <div className="space-y-4">
          <div className="space-y-1.5">
            <label className="text-xs uppercase tracking-[0.16em] text-muted-foreground">
              Email
            </label>
            <div className="relative">
              <Mail className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground/70" />
              <Input
                type="email"
                autoComplete="off"
                placeholder="agent@company.com"
                className="pl-9"
                value={email}
                onChange={(e) => setEmail(e.target.value)}
                autoFocus
              />
            </div>
          </div>

          <div className="space-y-1.5">
            <label className="text-xs uppercase tracking-[0.16em] text-muted-foreground">
              Initial password
            </label>
            <div className="relative">
              <KeyRound className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground/70" />
              <Input
                type={showPw ? "text" : "password"}
                autoComplete="new-password"
                placeholder="••••••••••••"
                className="pl-9 pr-9"
                value={password}
                onChange={(e) => setPassword(e.target.value)}
              />
              <button
                type="button"
                onClick={() => setShowPw((v) => !v)}
                className="absolute right-2 top-1/2 flex h-7 w-7 -translate-y-1/2 items-center justify-center rounded-md text-muted-foreground hover:bg-white/[0.04] hover:text-foreground"
                aria-label={showPw ? "Hide password" : "Show password"}
              >
                {showPw ? <EyeOff className="h-4 w-4" /> : <Eye className="h-4 w-4" />}
              </button>
            </div>
            <p
              className={cn(
                "text-[11px]",
                pwValid ? "text-muted-foreground" : "text-amber-300/90",
              )}
            >
              {password.length}/{MIN_PASSWORD_LENGTH}+ characters — share this
              securely; the user can change it after first sign-in.
            </p>
          </div>

          <div className="rounded-lg border border-white/[0.06] bg-white/[0.02] p-3">
            <p className="text-xs uppercase tracking-wide text-muted-foreground mb-2">
              Role
            </p>
            <div className="flex gap-2">
              {(["Agent", "Admin"] as const).map((r) => (
                <button
                  key={r}
                  type="button"
                  onClick={() => setRole(r)}
                  className={cn(
                    "flex-1 rounded-md border px-3 py-2 text-sm font-medium transition-colors",
                    role === r
                      ? "border-primary/50 bg-primary/15 text-primary"
                      : "border-white/[0.08] bg-white/[0.02] text-muted-foreground hover:bg-white/[0.04]",
                  )}
                >
                  {r}
                </button>
              ))}
            </div>
          </div>
        </div>

        <DialogFooter>
          <Button variant="ghost" onClick={() => onOpenChange(false)}>
            Cancel
          </Button>
          <Button
            onClick={() => add.mutate()}
            disabled={!formValid || add.isPending}
          >
            {add.isPending ? (
              <Loader2 className="mr-2 h-4 w-4 animate-spin" />
            ) : (
              <UserPlus className="mr-2 h-4 w-4" />
            )}
            Add user
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

// ---- utilities ---------------------------------------------------------

function useDebounced<T>(value: T, delayMs: number): T {
  const [debounced, setDebounced] = useState(value);
  const ref = useRef<number | undefined>(undefined);
  useEffect(() => {
    if (ref.current !== undefined) window.clearTimeout(ref.current);
    ref.current = window.setTimeout(() => setDebounced(value), delayMs);
    return () => {
      if (ref.current !== undefined) window.clearTimeout(ref.current);
    };
  }, [value, delayMs]);
  return debounced;
}

function formatRelative(utc: string): string {
  const date = new Date(utc);
  const diffMs = Date.now() - date.getTime();
  const minutes = Math.floor(diffMs / 60_000);
  if (minutes < 1) return "just now";
  if (minutes < 60) return `${minutes}m ago`;
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return `${hours}h ago`;
  const days = Math.floor(hours / 24);
  if (days < 30) return `${days}d ago`;
  return date.toLocaleDateString();
}
