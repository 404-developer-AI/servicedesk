import { Link, useParams, useSearch } from "@tanstack/react-router";
import { useMobileHeader } from "./mobileHeader";
import { MobileViewTicketsPage } from "./pages/MobileViewTicketsPage";
import { MobileTicketPage } from "./pages/MobileTicketPage";
import { MobileSearchPage } from "./pages/MobileSearchPage";

/// Route-level wrappers for the lazy /m chunk: read the URL, hand plain
/// props to the pages (keeps the pages free of router typing).

export { MobileShell } from "./MobileShell";
export { MobileViewsPage } from "./pages/MobileViewsPage";
export { MobileMentionsPage } from "./pages/MobileMentionsPage";
export { MobileMePage } from "./pages/MobileMePage";

export function MobileViewTicketsRoute() {
  const { viewId } = useParams({ strict: false }) as { viewId?: string };
  // Remount per view so search state and paging never leak between views.
  return <MobileViewTicketsPage key={viewId} viewId={viewId ?? "all"} />;
}

export function MobileTicketRoute() {
  const { ticketId } = useParams({ strict: false }) as { ticketId?: string };
  return ticketId ? <MobileTicketPage key={ticketId} ticketId={ticketId} /> : null;
}

export function MobileSearchRoute() {
  const { q } = useSearch({ strict: false }) as { q?: string };
  return <MobileSearchPage initialQuery={q} />;
}

/// Unknown /m path (typed or stale link): stay inside the mobile shell.
export function MobileNotFound() {
  useMobileHeader("Not found", "/m");
  return (
    <div className="py-10 text-center">
      <p className="text-sm text-muted-foreground">This page does not exist on the mobile version.</p>
      <Link to="/m" className="mt-3 inline-block text-sm font-medium text-primary">
        Go to Views
      </Link>
    </div>
  );
}
