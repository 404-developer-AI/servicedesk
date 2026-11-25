import { parseMetadata } from "../timelineMeta";
import type { TicketEvent } from "@/lib/ticket-api";
import { CollapsibleBody } from "./CollapsibleBody";
import { PostAttachmentStrip } from "./attachments";
import { readAttachmentsFromMeta } from "./attachmentMeta";
import { MailReceivedBody, MailSentBody } from "./mailEventBodies";
import {
  AssignmentText,
  ChecklistCloseBlockedBody,
  ChecklistEventBody,
  CreatedBody,
  IntakeFormBody,
  MetaChangeText,
  ParentLinkedBody,
  ParentUnlinkedBody,
  PendingTillChangeText,
  CampaignSentBody,
  CampaignAnswerBody,
  ProjectConvertedBody,
  ProjectLinkedBody,
  ProjectRevertedBody,
  ProjectUnlinkedBody,
  RequesterChangeBody,
  SystemNoteBody,
  TicketFlagChangeBody,
  TimeLimitAlertDismissedBody,
  TimeLimitExtendedBody,
  TimeLimitTrackingDisabledBody,
} from "./systemEventBodies";

/// `readOnly` (mobile web) renders the body without the mail actions
/// (reply / forward / split / .eml) — outbound mail is out of the mobile scope.
export function EventBody({ event, readOnly = false }: { event: TicketEvent; readOnly?: boolean }) {
  const meta = parseMetadata(event.metadataJson);

  switch (event.eventType) {
    case "Created":
      return <CreatedBody meta={meta} />;

    case "StatusChange":
      return <MetaChangeText meta={meta} fieldLabel="Status" />;

    case "AssignmentChange":
      return <AssignmentText meta={meta} />;

    case "PriorityChange":
      return <MetaChangeText meta={meta} fieldLabel="Priority" />;

    case "QueueChange":
      return <MetaChangeText meta={meta} fieldLabel="Queue" />;

    case "CategoryChange":
      return <MetaChangeText meta={meta} fieldLabel="Category" />;

    case "CompanyAssignment":
      return <MetaChangeText meta={meta} fieldLabel="Company" />;

    case "RequesterChange":
      return <RequesterChangeBody meta={meta} />;

    case "SystemNote":
      return <SystemNoteBody event={event} />;

    case "TimeLimitAlertDismissed":
      return <TimeLimitAlertDismissedBody meta={meta} />;

    case "TimeLimitTrackingDisabled":
      return <TimeLimitTrackingDisabledBody meta={meta} />;

    case "TimeLimitExtended":
      return <TimeLimitExtendedBody meta={meta} />;

    case "ChecklistAttached":
    case "ChecklistDetached":
    case "ChecklistCompleted":
    case "ChecklistReopened":
    case "ChecklistItemChanged":
      return <ChecklistEventBody event={event} meta={meta} />;

    case "ProjectConverted":
      return <ProjectConvertedBody />;

    case "ProjectReverted":
      return <ProjectRevertedBody />;

    case "ProjectLinked":
      return <ProjectLinkedBody meta={meta} />;

    case "TicketFlagChange":
      return <TicketFlagChangeBody meta={meta} />;

    case "PendingTillChange":
      return <PendingTillChangeText from={meta.from} to={meta.to} />;

    case "CampaignSent":
      return <CampaignSentBody meta={meta} />;

    case "CampaignAnswer":
      return <CampaignAnswerBody meta={meta} note={event.bodyText} />;

    case "ParentLinked":
      return <ParentLinkedBody meta={meta} />;

    case "ParentUnlinked":
      return <ParentUnlinkedBody />;

    case "ProjectUnlinked":
      return <ProjectUnlinkedBody />;

    case "ChecklistCloseBlocked":
      return <ChecklistCloseBlockedBody meta={meta} />;

    case "IntakeFormSent":
    case "IntakeFormSubmitted":
    case "IntakeFormExpired":
      return <IntakeFormBody event={event} meta={meta} />;

    case "MailSent":
      return <MailSentBody event={event} meta={meta} readOnly={readOnly} />;

    case "MailReceived":
      return <MailReceivedBody event={event} meta={meta} readOnly={readOnly} />;

    default: {
      const postAttachments = readAttachmentsFromMeta(meta);
      const hasBody = !!(event.bodyHtml || event.bodyText);
      if (!hasBody && postAttachments.length === 0) return null;
      return (
        <div className="space-y-2">
          <CollapsibleBody html={event.bodyHtml} text={event.bodyText} />
          <PostAttachmentStrip attachments={postAttachments} />
        </div>
      );
    }
  }
}
