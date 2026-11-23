import { cn } from "@/lib/utils";
import type { MailAddr } from "./mailAddress";

/// Slide-down header strip shown under a mail event's title row. Animated with
/// the grid-rows 0fr→1fr trick (pure CSS, no extra deps) so it expands from
/// zero height without a hard-coded max. Rows render only when populated, so
/// an inbound mail with no Bcc simply omits that line.
export function MailHeaderPanel({
  open,
  from,
  to,
  cc,
  bcc,
}: {
  open: boolean;
  from: MailAddr | null;
  to: MailAddr[];
  cc: MailAddr[];
  bcc: MailAddr[];
}) {
  const rows: Array<{ label: string; addrs: MailAddr[] }> = [];
  if (from) rows.push({ label: "From", addrs: [from] });
  if (to.length) rows.push({ label: "To", addrs: to });
  if (cc.length) rows.push({ label: "Cc", addrs: cc });
  if (bcc.length) rows.push({ label: "Bcc", addrs: bcc });

  return (
    <div
      className={cn(
        "grid transition-all duration-200 ease-out",
        open ? "grid-rows-[1fr] opacity-100 mb-2" : "grid-rows-[0fr] opacity-0"
      )}
      aria-hidden={!open}
    >
      <div className="overflow-hidden">
        <div className="rounded-lg border border-glass bg-glass px-3 py-2 text-xs space-y-1">
          {rows.map((row) => (
            <div key={row.label} className="flex gap-2">
              <span className="w-9 shrink-0 pt-0.5 text-[10px] font-medium uppercase tracking-wide text-muted-foreground/70">
                {row.label}
              </span>
              <span className="flex min-w-0 flex-wrap gap-x-1.5 gap-y-0.5">
                {row.addrs.map((a, i) => (
                  <a
                    key={`${a.address}-${i}`}
                    href={`mailto:${a.address}`}
                    title={a.name ? `${a.name} <${a.address}>` : a.address}
                    className="break-all text-foreground/80 transition-colors hover:text-primary hover:underline"
                  >
                    {a.name ? `${a.name} <${a.address}>` : a.address}
                    {i < row.addrs.length - 1 ? "," : ""}
                  </a>
                ))}
              </span>
            </div>
          ))}
        </div>
      </div>
    </div>
  );
}
