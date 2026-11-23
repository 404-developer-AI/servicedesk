import * as React from "react";

// ---- shared bits ------------------------------------------------------

export function SectionHeader({
  icon,
  title,
  description,
}: {
  icon: React.ReactNode;
  title: string;
  description: string;
}) {
  return (
    <div className="mb-4 flex items-center gap-3">
      <div className="rounded-md bg-glass p-2 text-primary">{icon}</div>
      <div>
        <h2 className="text-base font-semibold text-foreground">{title}</h2>
        <p className="text-xs text-muted-foreground">{description}</p>
      </div>
    </div>
  );
}

export function MissingEntry({ keyName }: { keyName: string }) {
  return (
    <p className="text-xs text-amber-200/70">
      Setting <code className="font-mono">{keyName}</code> not seeded yet —
      restart the API once to run the settings seeder.
    </p>
  );
}

// ---- Field-row layout helper -----------------------------------------

export function FieldRow({
  label,
  hint,
  children,
}: {
  label: string;
  hint: string;
  children: React.ReactNode;
}) {
  return (
    <div className="flex items-start justify-between gap-4 border-b border-glass py-3 last:border-b-0">
      <div className="min-w-0 flex-1">
        <p className="text-sm font-medium text-foreground">{label}</p>
        <p className="mt-0.5 text-xs text-muted-foreground">{hint}</p>
      </div>
      <div className="shrink-0">{children}</div>
    </div>
  );
}
