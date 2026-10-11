import * as React from "react";
import { Link } from "@tanstack/react-router";
import { useQuery } from "@tanstack/react-query";
import { ChevronRight, Inbox, LayoutList, Pin } from "lucide-react";
import { viewApi, type View } from "@/lib/ticket-api";
import { preferencesApi, settingsApi } from "@/lib/api";
import { Skeleton } from "@/components/ui/skeleton";
import { useMobileHeader } from "../mobileHeader";

/// /m — the agent's views. Filtering on mobile IS picking a view (views are
/// defined by admins, so no filter forms here); pinned views come first, in
/// the order the agent pinned them on desktop.
export function MobileViewsPage() {
  useMobileHeader("Views");
  const views = useQuery({ queryKey: ["views"], queryFn: viewApi.list, staleTime: 60_000 });
  const pinned = useQuery({
    queryKey: ["preferences", "pinned-views"],
    queryFn: preferencesApi.getPinnedViews,
    staleTime: 60_000,
  });
  const nav = useQuery({ queryKey: ["settings", "navigation"], queryFn: settingsApi.navigation, staleTime: 60_000 });

  const { pinnedViews, otherViews } = React.useMemo(() => {
    const all = [...(views.data ?? [])].sort((a, b) => a.sortOrder - b.sortOrder || a.name.localeCompare(b.name));
    const pinnedIds = pinned.data?.ids ?? [];
    const byId = new Map(all.map((v) => [v.id, v]));
    const pinnedViews = pinnedIds.map((id) => byId.get(id)).filter((v): v is View => !!v);
    const pinnedSet = new Set(pinnedViews.map((v) => v.id));
    return { pinnedViews, otherViews: all.filter((v) => !pinnedSet.has(v.id)) };
  }, [views.data, pinned.data]);

  if (views.isLoading) {
    return (
      <div className="space-y-2">
        {Array.from({ length: 6 }).map((_, i) => (
          <Skeleton key={i} className="h-14 w-full rounded-xl" />
        ))}
      </div>
    );
  }
  if (views.isError) {
    return <p className="py-10 text-center text-sm text-muted-foreground">Could not load your views. Try again in a moment.</p>;
  }

  const showAll = nav.data?.showOpenTickets ?? false;
  const nothing = !showAll && pinnedViews.length === 0 && otherViews.length === 0;

  return (
    <div className="space-y-5">
      {nothing && (
        <div className="glass-card flex flex-col items-center gap-2 px-6 py-10 text-center">
          <LayoutList className="h-6 w-6 text-muted-foreground" />
          <p className="text-sm font-medium">No views yet</p>
          <p className="text-xs text-muted-foreground">An admin can share views with you. Use Search to find a ticket meanwhile.</p>
        </div>
      )}
      {showAll && (
        <ViewSection>
          <ViewRow to="all" name="All tickets" icon={<Inbox className="h-[18px] w-[18px]" />} />
        </ViewSection>
      )}
      {pinnedViews.length > 0 && (
        <ViewSection label="Pinned">
          {pinnedViews.map((v) => (
            <ViewRow key={v.id} to={v.id} name={v.name} icon={<Pin className="h-[18px] w-[18px]" />} />
          ))}
        </ViewSection>
      )}
      {otherViews.length > 0 && (
        <ViewSection label={pinnedViews.length > 0 ? "All views" : undefined}>
          {otherViews.map((v) => (
            <ViewRow key={v.id} to={v.id} name={v.name} icon={<LayoutList className="h-[18px] w-[18px]" />} />
          ))}
        </ViewSection>
      )}
    </div>
  );
}

function ViewSection({ label, children }: { label?: string; children: React.ReactNode }) {
  return (
    <section className="space-y-2">
      {label && (
        <h2 className="px-1 text-[11px] font-semibold uppercase tracking-[0.14em] text-muted-foreground">{label}</h2>
      )}
      <div className="glass-card divide-y divide-glass overflow-hidden">{children}</div>
    </section>
  );
}

function ViewRow({ to, name, icon }: { to: string; name: string; icon: React.ReactNode }) {
  return (
    <Link
      to="/m/views/$viewId"
      params={{ viewId: to }}
      className="flex min-h-14 items-center gap-3 px-4 py-3 active:bg-glass-hover"
    >
      <span className="flex h-9 w-9 shrink-0 items-center justify-center rounded-lg bg-accent text-primary">{icon}</span>
      <span className="min-w-0 flex-1 truncate text-[15px] font-medium">{name}</span>
      <ChevronRight className="h-4 w-4 shrink-0 text-muted-foreground" />
    </Link>
  );
}
