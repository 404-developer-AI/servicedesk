import * as React from "react";
import { useServerOffsetMinutes, toServerLocal } from "@/hooks/useServerTime";

export function FieldLabel({ children }: { children: React.ReactNode }) {
  return (
    <div className="text-xs uppercase tracking-wider text-muted-foreground mb-1">
      {children}
    </div>
  );
}

export function FieldRow({
  icon: Icon,
  label,
  children,
}: {
  icon?: React.ComponentType<{ className?: string }>;
  label: string;
  children: React.ReactNode;
}) {
  return (
    <div>
      <div className="flex items-center gap-1.5 text-xs uppercase tracking-wider text-muted-foreground mb-1">
        {Icon && <Icon className="h-3 w-3" />}
        {label}
      </div>
      <div className="text-sm text-foreground/80">{children}</div>
    </div>
  );
}

export function ServerDate({ iso }: { iso: string | null | undefined }) {
  const serverOffsetMinutes = useServerOffsetMinutes();
  const offset = serverOffsetMinutes ?? 0;
  if (!iso) return <span>Not set</span>;
  return <span>{toServerLocal(iso, offset)}</span>;
}

export function EmptyState({ text }: { text: string }) {
  return (
    <div className="text-sm text-muted-foreground/60 italic py-4 text-center">
      {text}
    </div>
  );
}


/// v0.1.61 — back-office soft lock on one side-panel field: greyed and not
/// clickable while the ticket's category is Locked (UI only; the banner's
/// "Edit anyway" lifts it, the server never refuses the edit).
export function SoftLockedField({ locked, children }: { locked: boolean; children: React.ReactNode }) {
  return (
    <div
      className={locked ? "sd-soft-locked pointer-events-none select-none opacity-50" : undefined}
      aria-disabled={locked || undefined}
      title={locked ? "Handled via Back office forms" : undefined}
    >
      {children}
    </div>
  );
}
