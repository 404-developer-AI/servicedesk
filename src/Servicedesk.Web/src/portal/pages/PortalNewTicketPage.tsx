import { useState } from "react";
import { Link, useNavigate } from "@tanstack/react-router";
import { useQueryClient } from "@tanstack/react-query";
import { ArrowLeft } from "lucide-react";
import { toast } from "sonner";
import { Input } from "@/components/ui/input";
import { apiErrorMessage } from "@/lib/api";
import { portalTicketApi } from "@/lib/portal-api";
import { PortalComposer, htmlHasText, type PendingFile } from "@/portal/PortalComposer";
import { usePortalConfig } from "@/portal/PortalAuthLayout";
import { usePortalCompany, usePortalMe } from "@/portal/portalShared";

export function PortalNewTicketPage() {
  const navigate = useNavigate();
  const qc = useQueryClient();
  const config = usePortalConfig();
  const company = usePortalCompany();
  const [subject, setSubject] = useState("");
  const [body, setBody] = useState("");
  const [files, setFiles] = useState<PendingFile[]>([]);
  const [busy, setBusy] = useState<string | null>(null);
  const [subjectError, setSubjectError] = useState<string | null>(null);

  const me = usePortalMe();
  const enabled = (config.data?.enabled ? config.data.newTicketEnabled : true) && !me.user?.impersonated;

  async function submit() {
    const s = subject.trim();
    if (s.length === 0 || s.length > 300) {
      setSubjectError("Enter a short subject (max 300 characters).");
      return;
    }
    setSubjectError(null);
    if (!htmlHasText(body)) {
      toast.error("Describe your request first.");
      return;
    }
    setBusy("Creating ticket…");
    try {
      const created = await portalTicketApi.create(s, body, company.active?.id ?? null);
      let failed = 0;
      if (created.messageEventId !== null) {
        for (let i = 0; i < files.length; i++) {
          setBusy(`Uploading ${i + 1} of ${files.length}…`);
          try {
            await portalTicketApi.upload(created.id, created.messageEventId, files[i]!.file);
          } catch (e) {
            failed++;
            toast.error(`${files[i]!.file.name}: ${apiErrorMessage(e) ?? "upload failed"}`);
          }
        }
      }
      toast.success(failed ? `Ticket #${created.number} created, some attachments failed.` : `Ticket #${created.number} created.`);
      await qc.invalidateQueries({ queryKey: ["portal", "tickets"] });
      navigate({ to: "/portal/tickets/$ticketId", params: { ticketId: created.id } });
    } catch (e) {
      toast.error(apiErrorMessage(e) ?? "Could not create the ticket.");
    } finally {
      setBusy(null);
    }
  }

  return (
    <div className="mx-auto max-w-3xl space-y-5" data-testid="portal-new-ticket">
      <Link to="/portal" className="inline-flex items-center gap-1.5 text-xs text-muted-foreground hover:text-foreground">
        <ArrowLeft className="h-3.5 w-3.5" /> My tickets
      </Link>
      <div>
        <h1 className="font-display text-display-sm tracking-tight">New ticket</h1>
        <p className="mt-1 text-sm text-muted-foreground">
          Tell us what you need{company.active ? ` — we open it for ${company.active.name}` : ""}. You receive updates by mail and can follow the ticket here.
          {company.companies.length > 1 ? " Wrong company? Switch it in the bar above first." : ""}
        </p>
      </div>

      {!enabled ? (
        <div className="glass-card p-6 text-sm text-muted-foreground">
          Creating tickets from the portal is not available. Reply to an existing ticket or contact the service desk by mail.
        </div>
      ) : (
        <div className="glass-card space-y-4 p-5 sm:p-6">
          <div className="space-y-1.5">
            <label className="text-xs uppercase tracking-[0.16em] text-muted-foreground" htmlFor="portal-subject">
              Subject
            </label>
            <Input
              id="portal-subject"
              value={subject}
              onChange={(e) => setSubject(e.target.value)}
              placeholder="A short summary of your request"
              maxLength={300}
              disabled={busy !== null}
              autoFocus
            />
            {subjectError ? <p className="text-[11px] text-destructive/90">{subjectError}</p> : null}
          </div>
          <div className="space-y-1.5">
            <label className="text-xs uppercase tracking-[0.16em] text-muted-foreground">Description</label>
            <PortalComposer
              value={body}
              onChange={setBody}
              files={files}
              onFilesChange={setFiles}
              placeholder="What happened, what did you expect, and what have you tried? Screenshots and files can be attached below."
              minHeight="220px"
              disabled={busy !== null}
              busyLabel={busy}
              submitLabel="Create ticket"
              onSubmit={submit}
            />
          </div>
        </div>
      )}
    </div>
  );
}
