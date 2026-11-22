import * as React from "react";
import { useQuery } from "@tanstack/react-query";
import { AlertTriangle, CalendarCheck2, Loader2 } from "lucide-react";
import { useServerTimeZone } from "@/hooks/useServerTime";
import { formatDateTime } from "@/lib/dateFormat";
import { calendarApi } from "@/lib/forms-api";

/// v0.1.61 — live double-booking check under the appointment's start: the
/// picked technicians' Outlook free/busy for the chosen slot. Advice only —
/// it never blocks saving; overlaps are listed in one calm amber panel
/// (private items show as "Busy"), and the form's own appointment is left
/// out on an edit.
export function AppointmentAvailability({ userIds, startUtc, endUtc, excludeFormId }: {
  userIds: string[]; startUtc: string | null; endUtc: string | null; excludeFormId?: string | null;
}) {
  const zone = useServerTimeZone();
  const [debounced, setDebounced] = React.useState({ userIds, startUtc, endUtc });
  React.useEffect(() => {
    const t = setTimeout(() => setDebounced({ userIds, startUtc, endUtc }), 500);
    return () => clearTimeout(t);
  }, [userIds.join(","), startUtc, endUtc]); // eslint-disable-line react-hooks/exhaustive-deps

  const valid = debounced.userIds.length > 0 && !!debounced.startUtc && !!debounced.endUtc
    && Date.parse(debounced.endUtc!) > Date.parse(debounced.startUtc!);
  const q = useQuery({
    queryKey: ["calendar", "availability", debounced.userIds.join(","), debounced.startUtc, debounced.endUtc, excludeFormId ?? null],
    queryFn: () => calendarApi.availability(debounced.userIds, debounced.startUtc!, debounced.endUtc!, excludeFormId),
    enabled: valid,
    staleTime: 30_000,
    retry: false,
  });

  if (!valid) return null;
  if (q.isLoading) {
    return (
      <p className="sd-availability flex items-center gap-1.5 text-xs text-muted-foreground">
        <Loader2 className="h-3 w-3 animate-spin" /> Checking the technicians' calendars…
      </p>
    );
  }
  if (q.isError) return <p className="text-xs text-muted-foreground">The calendars could not be checked right now.</p>;
  const data = q.data;
  if (!data?.enabled) return null;
  if (data.error) return <p className="text-xs text-muted-foreground">Calendars could not be checked: {data.error}</p>;

  const clashes = data.users.filter((u) => u.items.length > 0);
  const unreachable = data.users.filter((u) => !u.reachable);
  if (clashes.length === 0) {
    return (
      <p className="sd-availability flex items-center gap-1.5 text-xs text-muted-foreground">
        <CalendarCheck2 className="h-3.5 w-3.5 text-primary" />
        {data.users.length === 1 ? "Free at this time." : "Everyone is free at this time."}
        {unreachable.length > 0 && ` (${unreachable.map((u) => u.name).join(", ")}: calendar not readable)`}
      </p>
    );
  }
  return (
    <div className="sd-availability rounded-md border border-amber-400/45 bg-amber-100/60 px-3 py-2.5 text-amber-900 dark:bg-amber-400/10 dark:text-amber-100">
      <p className="flex items-center gap-1.5 text-xs font-medium">
        <AlertTriangle className="h-3.5 w-3.5" /> Already booked at this time — you can still save.
      </p>
      <ul className="mt-1.5 space-y-1 text-xs">
        {clashes.map((u) => (
          <li key={u.userId}>
            <span className="font-medium">{u.name}</span>
            {u.items.map((i, n) => (
              <span key={n} className="block pl-3 opacity-90">
                {formatDateTime(i.startUtc, zone)} – {formatDateTime(i.endUtc, zone)}
                {" · "}{i.isPrivate || !i.subject ? "Busy" : i.subject}
                {i.location && !i.isPrivate ? ` · ${i.location}` : ""}
              </span>
            ))}
          </li>
        ))}
      </ul>
    </div>
  );
}
