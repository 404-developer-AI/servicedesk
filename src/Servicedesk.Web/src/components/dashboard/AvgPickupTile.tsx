import { useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { Clock } from "lucide-react";
import { slaApi } from "@/lib/api";
import { Skeleton } from "@/components/ui/skeleton";

const RANGES = [
  { label: "1d", days: 1 },
  { label: "7d", days: 7 },
  { label: "30d", days: 30 },
];

function formatMinutes(m: number | null): string {
  if (m === null) return "—";
  if (m < 60) return `${Math.round(m)}m`;
  const h = m / 60;
  if (h < 24) return `${h.toFixed(1)}h`;
  return `${(h / 24).toFixed(1)}d`;
}

export function AvgPickupTile() {
  const [days, setDays] = useState(1);
  const q = useQuery({
    queryKey: ["sla", "avg-pickup", days],
    queryFn: () => slaApi.avgPickup(days),
  });

  return (
    <section className="glass-card p-5">
      <header className="mb-3 flex items-center justify-between gap-2">
        <div className="flex items-center gap-2 text-sm font-medium text-foreground">
          <Clock className="h-4 w-4 text-primary" />
          Average first-response per queue
        </div>
        <div className="flex gap-1">
          {RANGES.map((r) => (
            <button
              key={r.days}
              type="button"
              onClick={() => setDays(r.days)}
              className={`rounded-md border px-2 py-0.5 text-xs transition ${
                days === r.days
                  ? "border-primary/40 bg-primary/10 text-foreground"
                  : "border-white/[0.06] bg-white/[0.02] text-muted-foreground hover:bg-white/[0.04]"
              }`}
            >
              {r.label}
            </button>
          ))}
        </div>
      </header>

      {q.isLoading ? (
        <Skeleton className="h-24 w-full" />
      ) : (
        <table className="w-full text-sm">
          <thead className="text-xs uppercase tracking-wider text-muted-foreground/60">
            <tr>
              <th className="py-1 text-left font-medium">Queue</th>
              <th className="py-1 text-right font-medium">Tickets</th>
              <th className="py-1 text-right font-medium">Avg pickup</th>
            </tr>
          </thead>
          <tbody>
            {q.data?.items.map((row) => (
              <tr key={row.queueId} className="border-t border-white/[0.04]">
                <td className="py-2 text-foreground">{row.queueName}</td>
                <td className="py-2 text-right text-muted-foreground">{row.ticketCount}</td>
                <td className="py-2 text-right font-mono text-foreground">
                  {formatMinutes(row.avgBusinessMinutes)}
                </td>
              </tr>
            ))}
            {(q.data?.items.length ?? 0) === 0 && (
              <tr>
                <td colSpan={3} className="py-6 text-center text-xs text-muted-foreground">
                  No data yet — SLA-tracked tickets will appear here.
                </td>
              </tr>
            )}
          </tbody>
        </table>
      )}
    </section>
  );
}
