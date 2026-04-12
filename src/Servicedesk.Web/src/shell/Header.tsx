import { useRouterState } from "@tanstack/react-router";
import { findNavItem } from "@/shell/navItems";

export function Header() {
  const pathname = useRouterState({ select: (s) => s.location.pathname });

  // Hide header on ticket detail pages — the ticket itself is the header
  const isTicketDetail = /^\/tickets\/[^/]+$/.test(pathname);
  if (isTicketDetail) return null;

  const current = findNavItem(pathname);
  const parent = !current
    ? findNavItem("/" + pathname.split("/").filter(Boolean)[0])
    : undefined;
  const title = current?.label ?? parent?.label ?? "Servicedesk";

  return (
    <header className="flex items-center gap-4 px-6 py-4" data-testid="app-header">
      <div className="min-w-0">
        <div className="truncate text-[11px] uppercase tracking-[0.22em] text-muted-foreground">Servicedesk</div>
        <h1 className="truncate font-display text-display-sm font-semibold">
          {title}
        </h1>
      </div>
    </header>
  );
}
