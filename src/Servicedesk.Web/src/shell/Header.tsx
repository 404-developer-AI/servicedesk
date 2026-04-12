import { useRouterState, useNavigate } from "@tanstack/react-router";
import { Search, Command as CommandIcon, LogOut, UserCircle2 } from "lucide-react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { Avatar, AvatarFallback } from "@/components/ui/avatar";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { findNavItem } from "@/shell/navItems";
import { useAuth, authStore } from "@/auth/authStore";
import { authApi } from "@/lib/api";
type HeaderProps = {
  onOpenCommandPalette: () => void;
};

export function Header({ onOpenCommandPalette }: HeaderProps) {
  const pathname = useRouterState({ select: (s) => s.location.pathname });
  const current = findNavItem(pathname);
  const parent = !current
    ? findNavItem("/" + pathname.split("/").filter(Boolean)[0])
    : undefined;
  const title = current?.label ?? parent?.label ?? "Servicedesk";
  const { user } = useAuth();
  const navigate = useNavigate();

  const handleLogout = async () => {
    try {
      await authApi.logout();
    } catch {
      // logout is idempotent locally even if the server call fails
    }
    authStore.patch({ user: null });
    toast.success("Signed out");
    navigate({ to: "/login" });
  };

  const initial = user?.email.slice(0, 1).toUpperCase() ?? "?";

  return (
    <header className="flex items-center justify-between gap-4 px-6 py-4" data-testid="app-header">
      <div className="min-w-0">
        <div className="truncate text-[11px] uppercase tracking-[0.22em] text-muted-foreground">Servicedesk</div>
        <h1 className="truncate font-display text-display-sm font-semibold">
          {title}
        </h1>
      </div>

      <div className="flex items-center gap-2">
        <Button
          variant="ghost"
          size="sm"
          onClick={onOpenCommandPalette}
          className="h-9 gap-2 rounded-full border border-white/10 bg-white/[0.03] px-3 text-xs text-muted-foreground hover:bg-white/[0.06] hover:text-foreground"
        >
          <Search className="h-3.5 w-3.5" />
          <span>Jump to…</span>
          <kbd className="ml-2 flex items-center gap-0.5 rounded border border-white/10 bg-white/[0.05] px-1.5 py-0.5 text-[10px] font-mono text-muted-foreground">
            <CommandIcon className="h-2.5 w-2.5" />K
          </kbd>
        </Button>

        {user && (
          <DropdownMenu>
            <DropdownMenuTrigger asChild>
              <button
                type="button"
                className="flex items-center gap-2 rounded-full border border-white/10 bg-white/[0.03] pl-1 pr-3 py-1 text-left transition-colors hover:bg-white/[0.06]"
                data-testid="profile-menu-trigger"
              >
                <Avatar className="h-7 w-7 border border-white/10 bg-white/[0.04]">
                  <AvatarFallback className="bg-transparent text-xs font-medium text-muted-foreground">
                    {initial}
                  </AvatarFallback>
                </Avatar>
                <span className="hidden max-w-[140px] truncate text-xs text-muted-foreground sm:inline">
                  {user.email}
                </span>
              </button>
            </DropdownMenuTrigger>
            <DropdownMenuContent align="end" className="w-56">
              <DropdownMenuLabel className="text-xs">
                <div className="truncate font-medium">{user.email}</div>
                <div className="truncate text-[10px] uppercase tracking-[0.14em] text-muted-foreground">
                  {user.role}
                </div>
              </DropdownMenuLabel>
              <DropdownMenuSeparator />
              <DropdownMenuItem onClick={() => navigate({ to: "/profile" })}>
                <UserCircle2 className="mr-2 h-4 w-4" /> Profile
              </DropdownMenuItem>
              <DropdownMenuItem onClick={handleLogout}>
                <LogOut className="mr-2 h-4 w-4" /> Sign out
              </DropdownMenuItem>
            </DropdownMenuContent>
          </DropdownMenu>
        )}
      </div>
    </header>
  );
}
