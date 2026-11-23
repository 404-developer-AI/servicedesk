import { Link } from "@tanstack/react-router";
import type { UseMutationResult } from "@tanstack/react-query";
import { FolderKanban, Microscope, PhoneCall } from "lucide-react";
import { cn } from "@/lib/utils";
import { colorPillStyle } from "@/lib/colorPill";
import { flagColors, useTicketFlagSettings } from "@/lib/ticketFlags";
import { useTheme } from "@/app/ThemeProvider";
import type { Ticket, TicketBody, TicketFieldUpdate, TicketMutationResult } from "@/lib/ticket-api";
import type { ChecklistSettings, TicketChecklist } from "@/lib/checklist-api";
import { IsoClassificationActions } from "@/components/IsoClassificationActions";
import { SlaPill } from "@/components/sla/SlaPill";
import { ChecklistHeaderButton } from "../components/checklists/ChecklistHeaderButton";
import { TicketOrdersButton } from "../components/TicketOrdersButton";
import { EditableDescription } from "./EditableDescription";
import { DescriptionAttachmentChip } from "./SplitBanners";
import { ExportPdfButton } from "./TicketHeaderParts";

export function DescriptionSection({
  ticketId,
  ticket,
  body,
  updateMutation,
  descriptionAttachments,
  projectsEnabled,
  projectTicketNumber,
  checklistsEnabled,
  checklistSettings,
  checklists,
  activeChecklistId,
  onOpenChecklist,
}: {
  ticketId: string;
  ticket: Ticket;
  body: TicketBody;
  updateMutation: UseMutationResult<TicketMutationResult, Error, TicketFieldUpdate>;
  descriptionAttachments: { id: string; name: string; mimeType: string; size: number; url: string }[];
  projectsEnabled: boolean;
  projectTicketNumber: string | null;
  checklistsEnabled: boolean;
  checklistSettings: ChecklistSettings | null;
  checklists: TicketChecklist[];
  activeChecklistId: string | null;
  onOpenChecklist: (checklistId?: string | null) => void;
}) {
  const theme = useTheme();
  const { data: flagSettings } = useTicketFlagSettings();
  const flagColor = flagColors(flagSettings);

  return (
    <div className="shrink-0 pb-4">
      <div className="flex items-center gap-2 mb-2">
        <span className="text-xs uppercase tracking-wider text-muted-foreground">
          Description
        </span>
        {/* v0.1.49 — per-ticket visibility: the portal blanks an
            internal description server-side. */}
        <button
          type="button"
          disabled={updateMutation.isPending}
          onClick={() => updateMutation.mutate({ descriptionInternal: !ticket.descriptionInternal })}
          title={
            ticket.descriptionInternal
              ? "Only agents see this description. Click to make it visible to the customer in the portal."
              : "The customer sees this description in the portal. Click to make it internal."
          }
          className={cn(
            "rounded px-1.5 py-0.5 text-[10px] font-medium border transition-colors disabled:opacity-60",
            ticket.descriptionInternal
              ? "border-amber-500/30 bg-amber-500/10 text-amber-300 hover:bg-amber-500/20"
              : "border-emerald-500/30 bg-emerald-500/15 text-emerald-300 hover:bg-emerald-500/25",
          )}
        >
          {ticket.descriptionInternal ? "Internal" : "Visible to customer"}
        </button>
        <div className="ml-auto flex items-center gap-3">
          {/* v0.0.105 — project marker: on the project ticket itself, and
              as a jump-chip on tickets linked to a project. */}
          {projectsEnabled && ticket.isProject && (
            <span
              className="flex shrink-0 items-center gap-1 rounded-md border border-sky-400/60 bg-sky-100/80 px-2 py-1 text-xs font-medium text-sky-800 dark:border-sky-500/30 dark:bg-sky-500/10 dark:text-sky-200"
              title="Internal project — customers cannot see this ticket"
            >
              <FolderKanban className="h-3 w-3" aria-hidden />
              Project
            </span>
          )}
          {projectsEnabled && !ticket.isProject && ticket.projectTicketId && projectTicketNumber && (
            <Link
              to="/tickets/$ticketId"
              params={{ ticketId: ticket.projectTicketId }}
              className="flex shrink-0 items-center gap-1 rounded-md border border-sky-400/60 bg-sky-100/80 px-2 py-1 text-xs font-medium text-sky-800 transition-colors hover:bg-sky-200/80 dark:border-sky-500/30 dark:bg-sky-500/10 dark:text-sky-200 dark:hover:bg-sky-500/20"
              title="Part of a project — open the project ticket"
            >
              <FolderKanban className="h-3 w-3" aria-hidden />
              Project #{projectTicketNumber}
            </Link>
          )}
          {/* v0.1.17 — Call-back / Research flag badges (internal). */}
          {ticket.isCallback && (
            <span
              className="flex shrink-0 items-center gap-1 rounded-md border px-2 py-1 text-xs font-medium"
              style={colorPillStyle(flagColor.callback, theme)}
              title="Call-back ticket — someone has to call this customer back"
            >
              <PhoneCall className="h-3 w-3" aria-hidden />
              Call-back
            </span>
          )}
          {ticket.isResearch && (
            <span
              className="flex shrink-0 items-center gap-1 rounded-md border px-2 py-1 text-xs font-medium"
              style={colorPillStyle(flagColor.research, theme)}
              title="Research ticket — under investigation"
            >
              <Microscope className="h-3 w-3" aria-hidden />
              Research
            </span>
          )}
          <IsoClassificationActions ticket={ticket} />
          {checklistsEnabled && checklistSettings && (
            <ChecklistHeaderButton
              ticketId={ticketId}
              checklists={checklists}
              maxPerTicket={checklistSettings.maxPerTicket}
              onOpen={() => onOpenChecklist(activeChecklistId)}
              onAttached={(c) => onOpenChecklist(c.id)}
            />
          )}
          {/* v0.1.59 — linked TD SYNNEX orders (self-gating on the flag). */}
          <TicketOrdersButton ticketId={ticketId} />
          <SlaPill ticketId={ticket.id} className="shrink-0 justify-end" />
          <ExportPdfButton ticketId={ticketId} />
        </div>
      </div>
      <EditableDescription
        html={body.bodyHtml}
        text={body.bodyText}
        onSave={async (bodyHtml, bodyText) => {
          await updateMutation.mutateAsync({ bodyHtml, bodyText });
        }}
      />
      {descriptionAttachments.length > 0 && (
        <div className="flex flex-wrap gap-2 pt-2">
          {descriptionAttachments.map((a) => (
            <DescriptionAttachmentChip key={a.id} attachment={a} />
          ))}
        </div>
      )}
    </div>
  );
}
