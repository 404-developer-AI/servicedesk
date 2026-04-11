import { useNavigate } from "@tanstack/react-router";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { ShieldCheck, ChevronDown } from "lucide-react";
import { ROLES, type Role } from "@/lib/roles";
import { devRoleStore } from "@/stores/useDevRoleStore";
import { useCurrentRole } from "@/hooks/useCurrentRole";

// Dev-only. Not mounted in production builds — see Header.tsx.
export function DevRoleSwitcher() {
  const current = useCurrentRole();
  const navigate = useNavigate();

  const handleSelect = (role: Role) => {
    devRoleStore.set(role);
    // Force the router to re-run beforeLoad gates on the new role so the
    // user isn't stranded on a page they can no longer access.
    navigate({ to: "/", replace: true });
  };

  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <Button
          variant="ghost"
          size="sm"
          className="h-8 gap-2 rounded-full border border-dashed border-amber-400/40 bg-amber-400/[0.06] px-3 text-xs text-amber-100/90 hover:bg-amber-400/[0.10] hover:text-amber-50"
          data-testid="dev-role-switcher"
        >
          <ShieldCheck className="h-3.5 w-3.5" />
          <span className="font-mono uppercase tracking-[0.14em]">{current}</span>
          <Badge variant="secondary" className="h-4 border-amber-400/30 bg-amber-400/[0.08] px-1 text-[9px] font-normal text-amber-200/90">
            dev
          </Badge>
          <ChevronDown className="h-3 w-3 opacity-70" />
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end" className="w-48">
        <DropdownMenuLabel className="text-xs text-muted-foreground">Switch role (dev)</DropdownMenuLabel>
        <DropdownMenuSeparator />
        {ROLES.map((role: Role) => (
          <DropdownMenuItem
            key={role}
            onClick={() => handleSelect(role)}
            className="cursor-pointer"
          >
            <span className={role === current ? "text-primary" : undefined}>{role}</span>
          </DropdownMenuItem>
        ))}
      </DropdownMenuContent>
    </DropdownMenu>
  );
}
