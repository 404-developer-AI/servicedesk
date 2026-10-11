import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Laptop, Loader2, LogOut, MonitorSmartphone, Smartphone, Tablet } from "lucide-react";
import { authApi, type MySession } from "@/lib/api";
import { formatDateTimeMedium } from "@/lib/dateFormat";
import { useServerTimeZone } from "@/hooks/useServerTime";
import { cn } from "@/lib/utils";

/// "Where you're signed in" — every open staff session of the current user,
/// with a per-device sign-out. Lives on Profile (desktop) and on the mobile
/// Me tab, so a lost or shared phone can be signed out from any other
/// device. Rows are addressed by their public handle; the session token
/// never reaches the browser.

type DeviceKind = "phone" | "tablet" | "desktop" | "unknown";

function describeDevice(ua: string | null): { label: string; kind: DeviceKind } {
  if (!ua) return { label: "Unknown device", kind: "unknown" };
  const os =
    /iPhone/.test(ua) ? "iPhone"
    : /iPad/.test(ua) ? "iPad"
    : /Android/.test(ua) ? "Android"
    : /Windows/.test(ua) ? "Windows"
    : /Macintosh|Mac OS X/.test(ua) ? "Mac"
    : /Linux/.test(ua) ? "Linux"
    : "Unknown OS";
  const browser =
    /Edg\//.test(ua) ? "Edge"
    : /SamsungBrowser\//.test(ua) ? "Samsung Internet"
    : /Firefox\/|FxiOS\//.test(ua) ? "Firefox"
    : /Chrome\/|CriOS\//.test(ua) ? "Chrome"
    : /Safari\//.test(ua) ? "Safari"
    : "Browser";
  const kind: DeviceKind =
    os === "iPhone" || (os === "Android" && /Mobile/.test(ua)) ? "phone"
    : os === "iPad" || os === "Android" ? "tablet"
    : os === "Unknown OS" ? "unknown"
    : "desktop";
  return { label: `${os} · ${browser}`, kind };
}

const ICONS: Record<DeviceKind, typeof Laptop> = {
  phone: Smartphone,
  tablet: Tablet,
  desktop: Laptop,
  unknown: MonitorSmartphone,
};

export function MySessionsCard({ className, touch = false }: { className?: string; touch?: boolean }) {
  const queryClient = useQueryClient();
  const zone = useServerTimeZone();
  const sessions = useQuery({ queryKey: ["auth", "sessions"], queryFn: authApi.sessions, staleTime: 15_000 });
  const refresh = () => queryClient.invalidateQueries({ queryKey: ["auth", "sessions"] });

  const revoke = useMutation({
    mutationFn: (publicId: string) => authApi.revokeSession(publicId),
    onSuccess: () => {
      toast.success("That device is signed out");
      void refresh();
    },
    onError: () => toast.error("Could not sign that device out"),
  });
  const revokeOthers = useMutation({
    mutationFn: () => authApi.revokeOtherSessions(),
    onSuccess: () => {
      toast.success("All other devices are signed out");
      void refresh();
    },
    onError: () => toast.error("Could not sign the other devices out"),
  });

  const items: MySession[] = sessions.data?.items ?? [];
  const others = items.filter((s) => !s.current);
  const rowHeight = touch ? "min-h-16" : "min-h-12";
  const buttonSize = touch ? "h-10 px-3.5 text-[13px]" : "h-8 px-2.5 text-xs";

  return (
    <section className={cn("glass-card overflow-hidden", className)}>
      <div className="flex items-start justify-between gap-3 px-4 pt-4 pb-3">
        <div>
          <h2 className="text-base font-semibold text-foreground">Where you're signed in</h2>
          <p className="mt-0.5 text-xs text-muted-foreground">
            Lost or shared a device? Sign it out here. A signed-out device has to sign in again.
          </p>
        </div>
      </div>
      {sessions.isLoading ? (
        <div className="flex justify-center py-6 text-muted-foreground">
          <Loader2 className="h-4 w-4 animate-spin" />
        </div>
      ) : sessions.isError ? (
        <p className="px-4 pb-4 text-sm text-muted-foreground">Could not load your sessions.</p>
      ) : (
        <ul className="divide-y divide-glass border-t border-glass">
          {items.map((s) => {
            const device = describeDevice(s.userAgent);
            const Icon = ICONS[device.kind];
            return (
              <li key={s.publicId} className={cn("flex items-center gap-3 px-4 py-2.5", rowHeight)}>
                <Icon className="h-5 w-5 shrink-0 text-muted-foreground" />
                <div className="min-w-0 flex-1">
                  <div className="flex items-center gap-2">
                    <span className="truncate text-sm font-medium">{device.label}</span>
                    {s.current && (
                      <span className="shrink-0 rounded-full bg-accent px-2 py-0.5 text-[10px] font-semibold uppercase tracking-wide text-primary">
                        This device
                      </span>
                    )}
                  </div>
                  <div className="truncate text-xs text-muted-foreground">
                    Active {formatDateTimeMedium(s.lastSeenUtc, zone)}
                    {s.ip ? ` · ${s.ip}` : ""}
                    {s.amr === "ext" ? " · Microsoft 365" : ""}
                  </div>
                </div>
                {!s.current && (
                  <button
                    type="button"
                    onClick={() => revoke.mutate(s.publicId)}
                    disabled={revoke.isPending}
                    className={cn(
                      "inline-flex shrink-0 items-center gap-1.5 rounded-md border border-glass bg-glass font-medium text-foreground transition-colors hover:bg-glass-hover disabled:opacity-50",
                      buttonSize,
                    )}
                  >
                    <LogOut className="h-3.5 w-3.5" />
                    Sign out
                  </button>
                )}
              </li>
            );
          })}
        </ul>
      )}
      {others.length > 1 && (
        <div className="border-t border-glass px-4 py-3">
          <button
            type="button"
            onClick={() => revokeOthers.mutate()}
            disabled={revokeOthers.isPending}
            className={cn(
              "inline-flex items-center gap-1.5 rounded-md font-medium text-destructive disabled:opacity-50",
              touch ? "h-10 text-[14px]" : "h-8 text-xs",
            )}
          >
            <LogOut className="h-3.5 w-3.5" />
            Sign out all other devices
          </button>
        </div>
      )}
    </section>
  );
}
