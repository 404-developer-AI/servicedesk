import { FileSignature, Link2, type LucideIcon } from "lucide-react";

/// v0.0.76 — Contracts hub. Pure tile launcher: every contract module gets a
/// tile here that opens its own sub-page. This release ships the hub shell
/// only — both modules below are placeholders until their data model lands,
/// so the tiles render as non-interactive "coming soon" cards.
type ContractModule = {
  id: string;
  title: string;
  description: string;
  icon: LucideIcon;
};

const MODULES: readonly ContractModule[] = [
  {
    id: "overview",
    title: "Contracts overview",
    description: "All customer contracts in one place — terms, coverage and renewal dates.",
    icon: FileSignature,
  },
  {
    id: "m365-matching",
    title: "Microsoft 365 matching",
    description: "Match contracts against Microsoft 365 licenses and spot mismatches.",
    icon: Link2,
  },
];

export function ContractsPage() {
  return (
    <div className="flex flex-1 flex-col gap-4">
      <div className="flex items-center justify-between gap-3">
        <h1 className="text-display-md font-semibold text-foreground">Contracts</h1>
      </div>

      <div className="grid grid-cols-1 gap-4 md:grid-cols-2 lg:grid-cols-3">
        {MODULES.map((module) => (
          <ModuleTile key={module.id} module={module} />
        ))}
      </div>
    </div>
  );
}

/// A single module tile. Until the module ships this is intentionally not a
/// link — no hover lift, no pointer cursor — so the "coming soon" state reads
/// as a preview rather than a broken button.
function ModuleTile({ module }: { module: ContractModule }) {
  const Icon = module.icon;
  return (
    <div className="glass-card relative flex flex-col gap-3 p-5">
      <div className="flex items-start justify-between gap-3">
        <div className="flex h-10 w-10 items-center justify-center rounded-xl border border-glass bg-glass">
          <Icon className="h-5 w-5 text-primary" />
        </div>
        <span className="rounded-full border border-glass bg-glass px-2.5 py-0.5 text-[10px] font-medium uppercase tracking-widest text-muted-foreground">
          Coming soon
        </span>
      </div>
      <div className="space-y-1">
        <h2 className="text-sm font-semibold text-foreground/80">{module.title}</h2>
        <p className="text-xs leading-relaxed text-muted-foreground">{module.description}</p>
      </div>
    </div>
  );
}
