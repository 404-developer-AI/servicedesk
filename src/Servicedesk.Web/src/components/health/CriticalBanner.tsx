import { useQuery } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import { AlertTriangle } from "lucide-react";
import { systemApi } from "@/lib/api";
import { authStore } from "@/auth/authStore";

const HEALTH_QUERY_KEY = ["system", "health"] as const;

/// Red shell banner, admin-only, shows on any Critical subsystem. Shares
/// the HealthPill's query key so both update on the same 30s cadence.
export function CriticalBanner() {
  const role = authStore.get().user?.role;
  const { data } = useQuery({
    queryKey: HEALTH_QUERY_KEY,
    queryFn: () => systemApi.health(),
    refetchInterval: 30_000,
    enabled: role === "Admin",
  });

  if (role !== "Admin" || data?.status !== "Critical") return null;

  return (
    <div className="border-b border-rose-500/40 bg-rose-950/60 px-6 py-2 text-sm text-rose-100 backdrop-blur">
      <div className="flex items-center gap-3">
        <AlertTriangle className="h-4 w-4 shrink-0 text-rose-300" />
        <span className="flex-1">
          A critical subsystem needs attention. Mail intake or other automation may be paused.
        </span>
        <Link
          to="/settings/health"
          className="rounded-md border border-rose-400/40 bg-rose-500/10 px-3 py-1 text-xs font-medium text-rose-100 transition-colors hover:bg-rose-500/20"
        >
          Open health page
        </Link>
      </div>
    </div>
  );
}
