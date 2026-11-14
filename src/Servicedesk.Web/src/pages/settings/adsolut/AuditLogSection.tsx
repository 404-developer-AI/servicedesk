import { IntegrationAuditLog } from "@/components/integrations/IntegrationAuditLog";

export function AuditLogSection() {
  return (
    <section className="rounded-lg border border-glass-strong bg-glass p-5">
      <header className="mb-4 space-y-1">
        <h2 className="text-xs font-medium uppercase tracking-widest text-muted-foreground/60">
          Audit log
        </h2>
        <p className="text-xs text-muted-foreground">
          Every outbound call to Wolters Kluwer plus each healthcheck tick lands here with
          its latency and any upstream error. Distinct from the security audit log on
          <span className="font-mono"> /settings/audit</span> — this view tracks
          integration health, not admin actions.
        </p>
      </header>
      <IntegrationAuditLog integration="adsolut" />
    </section>
  );
}
