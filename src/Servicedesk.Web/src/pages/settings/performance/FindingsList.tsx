import * as React from "react";
import { ChevronDown, ChevronRight, Code2, Database, Globe, HardDrive, Layers, Server, Workflow } from "lucide-react";
import type { CodeRef, Finding } from "@/lib/perf-api";
import { cn } from "@/lib/utils";
import { Code, Pill, SqlBlock } from "./PerfUi";
import { severityTone } from "./perfFormat";

const CATEGORY_LABEL: Record<Finding["category"], string> = {
  hosting: "Hosting",
  network: "Network",
  code: "Backend code",
  database: "Database",
  maintenance: "DB maintenance",
  frontend: "Frontend",
  background: "Background",
};

const REF_LABEL: Record<string, string> = {
  route: "Route",
  fingerprint: "Query",
  pg_statement: "Statement",
  table: "Table",
  index: "Index",
  worker: "Worker",
  "frontend-route": "Screen",
  script: "Script",
  host: "Host",
  hub: "Hub",
  chunk: "Chunk",
  "search-source": "Search source",
};

function refIcon(kind: string) {
  switch (kind) {
    case "route":
    case "hub":
      return <Workflow className="h-3.5 w-3.5" />;
    case "fingerprint":
    case "pg_statement":
    case "table":
    case "index":
      return <Database className="h-3.5 w-3.5" />;
    case "worker":
      return <Layers className="h-3.5 w-3.5" />;
    case "frontend-route":
    case "script":
    case "chunk":
      return <Globe className="h-3.5 w-3.5" />;
    case "host":
      return <Server className="h-3.5 w-3.5" />;
    default:
      return <Code2 className="h-3.5 w-3.5" />;
  }
}

export function FindingCard({ finding, defaultOpen = false, rank }: { finding: Finding; defaultOpen?: boolean; rank?: number }) {
  const [open, setOpen] = React.useState(defaultOpen);
  const tone = severityTone(finding.severity);
  return (
    <article className="rounded-lg border border-glass bg-glass transition-colors hover:border-glass-strong">
      <button type="button" onClick={() => setOpen((v) => !v)} aria-expanded={open}
        className="flex w-full items-start gap-3 px-4 py-3 text-left">
        {rank !== undefined ? (
          <span className="mt-0.5 w-5 shrink-0 text-right font-display text-sm font-semibold tabular-nums text-muted-foreground">{rank}</span>
        ) : null}
        <div className="min-w-0 flex-1">
          <div className="flex flex-wrap items-center gap-1.5">
            <Pill tone={tone}>{finding.severity}</Pill>
            <span className="text-[11px] uppercase tracking-wide text-muted-foreground">{CATEGORY_LABEL[finding.category]}</span>
          </div>
          <h4 className="mt-1 text-sm font-medium leading-snug text-foreground">{finding.title}</h4>
        </div>
        <div className="flex shrink-0 flex-col items-end gap-0.5">
          <span className="text-[11px] text-muted-foreground">impact</span>
          <span className="text-sm font-semibold tabular-nums text-foreground">{finding.impactLabel}</span>
        </div>
        {open ? <ChevronDown className="mt-1 h-4 w-4 shrink-0 text-muted-foreground" /> : <ChevronRight className="mt-1 h-4 w-4 shrink-0 text-muted-foreground" />}
      </button>
      {open ? (
        <div className="space-y-3 border-t border-glass px-4 py-3 text-sm">
          <p className="leading-relaxed text-foreground/90">{finding.explanation}</p>
          {finding.evidence.length > 0 ? (
            <dl className="grid gap-x-6 gap-y-1 text-xs sm:grid-cols-2">
              {finding.evidence.map((e) => (
                <div key={e.label} className="flex justify-between gap-3 border-b border-glass py-1">
                  <dt className="text-muted-foreground">{e.label}</dt>
                  <dd className="text-right tabular-nums text-foreground">{e.value}</dd>
                </div>
              ))}
            </dl>
          ) : null}
          <div className="grid gap-3 md:grid-cols-2">
            <div>
              <div className="mb-1 text-xs font-medium text-muted-foreground">Likely cause</div>
              <p className="text-xs leading-relaxed text-foreground/90">{finding.cause}</p>
            </div>
            <div>
              <div className="mb-1 text-xs font-medium text-muted-foreground">What to do</div>
              <p className="text-xs leading-relaxed text-foreground/90">{finding.action}</p>
            </div>
          </div>
          {finding.codeRefs.length > 0 ? (
            <div>
              <div className="mb-1.5 text-xs font-medium text-muted-foreground">Where to look</div>
              <ul className="space-y-2">
                {finding.codeRefs.map((r, i) => (
                  <CodeRefItem key={`${r.kind}:${r.value}:${i}`} codeRef={r} />
                ))}
              </ul>
            </div>
          ) : null}
        </div>
      ) : null}
    </article>
  );
}

function CodeRefItem({ codeRef }: { codeRef: CodeRef }) {
  return (
    <li className="space-y-1">
      <div className="flex flex-wrap items-center gap-1.5 text-xs">
        <span className="inline-flex items-center gap-1 text-muted-foreground">
          {refIcon(codeRef.kind)}
          {REF_LABEL[codeRef.kind] ?? codeRef.kind}
        </span>
        <Code>{codeRef.value}</Code>
        {codeRef.caller ? (
          <span className="text-muted-foreground">
            called from <Code>{codeRef.caller}</Code>
          </span>
        ) : null}
      </div>
      {codeRef.sql ? <SqlBlock sql={codeRef.sql} maxHeight="8rem" /> : null}
    </li>
  );
}

export function FindingsList({
  findings,
  limit,
  emptyText = "No bottlenecks detected in this period.",
}: {
  findings: Finding[];
  limit?: number;
  emptyText?: string;
}) {
  const [all, setAll] = React.useState(false);
  const shown = limit && !all ? findings.slice(0, limit) : findings;
  if (findings.length === 0) {
    return (
      <div className="flex items-center gap-2 rounded-lg border border-glass bg-glass px-4 py-6 text-sm text-muted-foreground">
        <HardDrive className="h-4 w-4" /> {emptyText}
      </div>
    );
  }
  return (
    <div className="flex flex-col gap-2">
      {shown.map((f, i) => (
        <FindingCard key={f.key} finding={f} rank={i + 1} defaultOpen={i === 0 && f.severity !== "info"} />
      ))}
      {limit && findings.length > limit ? (
        <button type="button" onClick={() => setAll((v) => !v)}
          className={cn("self-start text-xs text-primary hover:underline")}>
          {all ? "Show fewer" : `Show all ${findings.length} findings`}
        </button>
      ) : null}
    </div>
  );
}
