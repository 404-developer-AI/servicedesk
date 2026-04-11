import { useSystemVersion } from "@/hooks/useSystemVersion";
import { useServerTime, formatServerLocalClock, formatServerLocalDate } from "@/hooks/useServerTime";

export function Footer() {
  const version = useSystemVersion();
  const { time, error } = useServerTime();

  const versionLabel = version.data
    ? `v${version.data.version} · ${version.data.commit}`
    : version.isError
      ? "version unavailable"
      : "…";

  const clock = time ? formatServerLocalClock(time) : "…";
  const date = time ? formatServerLocalDate(time) : "…";
  const tz = time?.timezone ?? "…";

  return (
    <footer
      className="glass-panel mx-4 mb-3 flex items-center justify-between gap-4 px-4 py-2 text-xs text-muted-foreground"
      data-testid="app-footer"
    >
      <div className="flex items-center gap-2">
        <span className="inline-block h-1.5 w-1.5 rounded-full bg-primary/80 shadow-[0_0_8px_hsl(var(--primary))]" />
        <span data-testid="footer-version" className="font-mono">{versionLabel}</span>
      </div>
      <div className="flex items-center gap-3 font-mono">
        {error && <span className="text-destructive/80">server time unavailable</span>}
        <span className="text-muted-foreground/70">{tz}</span>
        <span data-testid="footer-server-time" className="text-foreground/90">
          {date} {clock}
        </span>
      </div>
    </footer>
  );
}
