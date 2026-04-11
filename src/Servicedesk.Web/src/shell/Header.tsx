import { useRouterState } from "@tanstack/react-router";
import { Search, Command as CommandIcon } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Avatar, AvatarFallback } from "@/components/ui/avatar";
import { findNavItem } from "@/shell/navItems";
import { DevRoleSwitcher } from "@/shell/DevRoleSwitcher";
import { useCurrentRole } from "@/hooks/useCurrentRole";

type HeaderProps = {
  onOpenCommandPalette: () => void;
};

export function Header({ onOpenCommandPalette }: HeaderProps) {
  const pathname = useRouterState({ select: (s) => s.location.pathname });
  const current = findNavItem(pathname);
  const role = useCurrentRole();

  return (
    <header className="flex items-center justify-between gap-4 px-6 py-4" data-testid="app-header">
      <div className="min-w-0">
        <div className="truncate text-[11px] uppercase tracking-[0.22em] text-muted-foreground">Servicedesk</div>
        <h1 className="truncate font-display text-display-sm font-semibold">
          {current?.label ?? "Not found"}
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

        {import.meta.env.DEV && <DevRoleSwitcher />}

        <Avatar className="h-9 w-9 border border-white/10 bg-white/[0.04]">
          <AvatarFallback className="bg-transparent text-xs font-medium text-muted-foreground">
            {role.slice(0, 1)}
          </AvatarFallback>
        </Avatar>
      </div>
    </header>
  );
}
