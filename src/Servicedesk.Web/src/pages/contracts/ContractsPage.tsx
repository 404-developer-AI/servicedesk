import { FileSignature, Link2, Package, Settings2, type LucideIcon } from "lucide-react";
import { Link } from "@tanstack/react-router";

/// v0.0.76 — Contracts hub. Tile launcher: every contract module gets a tile
/// here that opens its own sub-page. Tiles with a `to` are live and link
/// through; the rest render as non-interactive "coming soon" cards until their
/// data model lands.
type ContractModule = {
  id: string;
  title: string;
  description: string;
  icon: LucideIcon;
  /** Live route — present = the tile links through; absent = "coming soon". */
  to?: string;
};

const MODULES: readonly ContractModule[] = [
  {
    id: "articles",
    title: "Contract Articles",
    description: "The Adsolut article catalogue — codes, names and VAT, mirrored from the ERP.",
    icon: Package,
    to: "/contracts/articles",
  },
  {
    id: "overview",
    title: "Contracts overview",
    description: "All customer contracts in one place — terms, coverage and renewal dates.",
    icon: FileSignature,
    to: "/contracts/overview",
  },
  {
    id: "m365-matching",
    title: "Microsoft 365 matching",
    description: "Match contracts against Microsoft 365 licenses and spot mismatches.",
    icon: Link2,
    to: "/contracts/m365-matching",
  },
  {
    id: "settings",
    title: "Contract settings",
    description: "Report email templates and reporting contacts.",
    icon: Settings2,
    to: "/contracts/settings",
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

/// A single module tile. A live module (with `to`) links through with a hover
/// lift; a placeholder is intentionally not a link — no hover, no pointer
/// cursor — so the "coming soon" state reads as a preview rather than a broken
/// button.
function ModuleTile({ module }: { module: ContractModule }) {
  const Icon = module.icon;
  const body = (
    <>
      <div className="flex items-start justify-between gap-3">
        <div className="flex h-10 w-10 items-center justify-center rounded-xl border border-glass bg-glass">
          <Icon className="h-5 w-5 text-primary" />
        </div>
        {!module.to && (
          <span className="rounded-full border border-glass bg-glass px-2.5 py-0.5 text-[10px] font-medium uppercase tracking-widest text-muted-foreground">
            Coming soon
          </span>
        )}
      </div>
      <div className="space-y-1">
        <h2 className={module.to ? "text-sm font-semibold text-foreground" : "text-sm font-semibold text-foreground/80"}>
          {module.title}
        </h2>
        <p className="text-xs leading-relaxed text-muted-foreground">{module.description}</p>
      </div>
    </>
  );

  if (module.to) {
    return (
      <Link
        to={module.to}
        className="glass-card relative flex flex-col gap-3 p-5 transition-transform hover:-translate-y-0.5 hover:border-glass-strong"
      >
        {body}
      </Link>
    );
  }

  return <div className="glass-card relative flex flex-col gap-3 p-5">{body}</div>;
}
