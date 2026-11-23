using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Servicedesk.Api.Auth;
using Servicedesk.Api.Presence;
using Servicedesk.Domain.Tickets;
using Servicedesk.Infrastructure.Access;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Auth;
using Servicedesk.Infrastructure.KnowledgeBase;
using Servicedesk.Infrastructure.Mail.Attachments;
using Servicedesk.Infrastructure.Mail.Graph;
using Servicedesk.Infrastructure.Mail.Ingest;
using Servicedesk.Infrastructure.Mail.Outbound;
using Servicedesk.Infrastructure.Notifications;
using Servicedesk.Infrastructure.Persistence.Companies;
using Servicedesk.Infrastructure.Persistence.Taxonomy;
using Servicedesk.Infrastructure.Persistence.Tickets;
using Servicedesk.Infrastructure.Realtime;
using Servicedesk.Infrastructure.Settings;
using Servicedesk.Infrastructure.Signatures;
using Servicedesk.Infrastructure.Sla;
using Servicedesk.Infrastructure.Surveys;
using Servicedesk.Infrastructure.TaggingMailboxes;
using Servicedesk.Infrastructure.Tickets;
using Servicedesk.Infrastructure.Triggers;
using Servicedesk.Infrastructure.Triggers.StatusGate;
using Servicedesk.Infrastructure.Triggers.FirstOpenGate;
using Servicedesk.Infrastructure.Workflow;

namespace Servicedesk.Api.Tickets;

public static partial class TicketEndpoints
{
    public sealed record CreateTicketRequest(
        [property: Required] string Subject,
        string? BodyText,
        string? BodyHtml,
        Guid RequesterContactId,
        Guid QueueId,
        Guid StatusId,
        Guid PriorityId,
        Guid? CategoryId,
        Guid? AssigneeUserId,
        string? Source,
        DateTime? PendingTillUtc = null,
        Guid? ParentTicketId = null,
        // v0.0.39 — caller selects the ticket type when known. Null
        // defaults to 'support' in the repository so the existing
        // "+ New ticket" flow keeps working unchanged.
        Guid? TicketTypeId = null,
        // v0.0.39 — optional first event written immediately after the
        // ticket exists. Used by the "Create linked X ticket" flow to
        // drop an opening note (internal or public) into the timeline
        // alongside the ticket body.
        InitialNoteRequest? InitialNote = null,
        // v0.0.51 — agent-supplied company at create-time (UI flow).
        // When set the handler skips the ContactLookupService cascade
        // and freezes this company on the new ticket with
        // company_resolved_via='manual'. Mail-intake leaves this null
        // and keeps the cascade. NewLinkRole pairs with CompanyId:
        //   * required ('primary' | 'secondary' | 'supplier') when the
        //     picked company is not yet on the contact's link list —
        //     the handler upserts the link with that role so future
        //     intake knows the relationship;
        //   * must be null when the picked company is already linked
        //     to the contact (no learn-flow needed; trying to set it
        //     yields 400).
        // 'primary' conflicts with an existing primary link to a
        // different company → 409.
        Guid? CompanyId = null,
        string? NewLinkRole = null,
        // v0.0.105 — create as a project ticket (the "Project ticket"
        // toggle in the new-ticket drawer). Default off.
        bool IsProject = false,
        // v0.1.17 — create with the Call-back flag on (new-ticket drawer).
        bool IsCallback = false);

    public sealed record InitialNoteRequest(string BodyHtml, bool IsInternal);

    /// v0.0.102 — bulk action payload. Everything but <c>TicketIds</c> is
    /// optional; the service rejects a request that changes nothing.
    /// <c>MessageIsInternal</c> defaults to true (internal note) so a
    /// caller that forgets the flag can never accidentally post a public
    /// comment on N tickets.
    public sealed record BulkTicketActionRequest(
        IReadOnlyList<Guid> TicketIds,
        string? MessageHtml = null,
        bool? MessageIsInternal = null,
        Guid? StatusId = null,
        Guid? QueueId = null,
        Guid? PriorityId = null,
        Guid? AssigneeUserId = null,
        bool UnassignAssignee = false,
        DateTime? PendingTillUtc = null);

    public sealed record UpdateTicketRequest(
        Guid? QueueId,
        Guid? StatusId,
        Guid? PriorityId,
        Guid? CategoryId,
        Guid? AssigneeUserId,
        string? Subject,
        string? BodyText,
        string? BodyHtml,
        DateTime? PendingTillUtc = null,
        // v0.0.78 — explicit unassign. AssigneeUserId null is
        // indistinguishable from "field omitted", so the FE sends this
        // flag (instead of a null assigneeUserId) when the agent picks
        // "Unassigned". Mutually exclusive with AssigneeUserId.
        bool UnassignAssignee = false,
        // v0.0.42 — gate confirmations the agent submitted in the
        // status-change dialog. Each entry pairs a gate trigger id with
        // the optional textarea answer. Server re-evaluates the gate set
        // before applying the mutation; missing or empty-required
        // entries cause a 409 + the list of unsatisfied gates.
        IReadOnlyList<GateConfirmationInput>? GateConfirmations = null,
        // v0.1.17 — Call-back / Research flag toggles (Status panel and the
        // call-back prompt on open). Null = not provided.
        bool? IsCallback = null,
        bool? IsResearch = null,
        // v0.1.49 — description visibility toggle (Internal / Visible to
        // customer). Null = not provided.
        bool? DescriptionInternal = null,
        // v0.1.61 — explicit "no category" (Category field in the Status
        // panel). Mutually exclusive with CategoryId.
        bool ClearCategory = false);

    /// One agent-supplied confirmation payload for a status-change gate.
    /// The fields are kind-specific:
    ///   * <c>prompt_confirm</c> gate → <see cref="Answers"/> is keyed by
    ///     the gate's per-question <c>key</c>; values come from the
    ///     dialog (typed text for text questions, the clicked button's
    ///     label for yesno questions).
    ///   * <c>contact_company_link</c> gate → <see cref="CompanyId"/> +
    ///     <see cref="Role"/> identify the contact-company link the
    ///     agent picked in the dialog. The PATCH handler creates the
    ///     link before applying the status flip; an invalid company or
    ///     role causes the PATCH to fail with 400 or 409 depending on
    ///     the kind of mismatch.
    /// Missing or incomplete payloads cause the PATCH to 409 with
    /// <c>status_gate_required</c> + the unsatisfied gates list.
    public sealed record GateConfirmationInput(
        Guid TriggerId,
        Dictionary<string, string>? Answers,
        Guid? CompanyId = null,
        string? Role = null);

    /// Agent's confirmation of a first-open title-review gate. Carries the
    /// matched gate's trigger id (echoed back for server-side re-validation)
    /// and the reviewed subject — unchanged when the agent deemed the
    /// original title fine, edited otherwise. The server trims + length-
    /// caps it and re-evaluates the gate before applying.
    public sealed record ConfirmOpenGateRequest(
        Guid TriggerId,
        string? Subject);

    public sealed record AddEventRequest(
        string? EventType,
        string? BodyText,
        string? BodyHtml,
        bool? IsInternal,
        IReadOnlyList<Guid>? AttachmentIds = null,
        IReadOnlyList<Guid>? MentionedUserIds = null,
        // v0.0.38 — compose templates inserted into this event. Any template
        // with a non-null linked_survey_id triggers a SurveyDispatchService
        // call after the event lands. Idempotent: dispatch skips when an
        // active invitation already exists for (survey, ticket).
        IReadOnlyList<Guid>? ComposeTemplateIds = null,
        // v0.0.60 — tagging-only mailbox ids tagged via @@-mention. Filtered
        // to active rows server-side; each gets a notification mail.
        IReadOnlyList<Guid>? MentionedMailboxIds = null,
        // v0.1.54 — group-tag ids tagged via @@-mention (one shared tag per
        // group, never a mail).
        IReadOnlyList<Guid>? MentionedGroupIds = null);

    public sealed record UpdateEventRequest(
        string? BodyText,
        string? BodyHtml,
        bool? IsInternal,
        IReadOnlyList<Guid>? AttachmentIds = null);

    public sealed record SendOutboundMailRequest(
        string Kind,
        IReadOnlyList<MailRecipientInput>? To,
        IReadOnlyList<MailRecipientInput>? Cc,
        IReadOnlyList<MailRecipientInput>? Bcc,
        string? Subject,
        string? BodyHtml,
        IReadOnlyList<Guid>? AttachmentIds = null,
        IReadOnlyList<Guid>? MentionedUserIds = null,
        IReadOnlyList<Guid>? LinkedFormIds = null,
        // v0.0.38 — compose templates used to build this mail; any with a
        // linked survey dispatch after a successful send.
        IReadOnlyList<Guid>? ComposeTemplateIds = null,
        // v0.0.60 — tagging-only mailbox ids tagged via @@-mention.
        IReadOnlyList<Guid>? MentionedMailboxIds = null,
        // v0.1.54 — group-tag ids tagged via @@-mention.
        IReadOnlyList<Guid>? MentionedGroupIds = null,
        // v0.0.61 — the compose window pre-loaded the signature as a fixed
        // block, so the body carries a data-sd-signature marker the send path
        // swaps for the real signature (above the quoted history).
        bool SignaturePreloaded = false);

    public sealed record MailRecipientInput(string Address, string? Name);

    public sealed record PinEventRequest(string? Remark);

    /// v0.0.9 ToDo #4 / v0.0.51 — manual company-assignment payload.
    /// <c>NewLinkRole</c> is required when the picked company is not
    /// yet on the contact's link list ('primary' | 'secondary' |
    /// 'supplier'); it must be null when the company is already
    /// linked (no learn-flow needed). 'primary' is rejected with 409
    /// when the contact already has a primary on a different company.
    public sealed record AssignTicketCompanyRequest(
        Guid CompanyId,
        string? NewLinkRole);

    public sealed record UpdatePinRemarkRequest(string Remark);

    /// v0.0.12: payload for switching a ticket's requester to another contact.
    /// The company re-resolves server-side via <see cref="IContactLookupService"/>,
    /// so the client only needs to pass the target contact id.
    public sealed record ChangeTicketRequesterRequest(Guid ContactId);

    public sealed record LinkParentRequest(Guid ParentTicketId);

    /// v0.0.23: payload for merging this ticket into another. The acknowledge
    /// flag is required only when the dialog detected a cross-customer or
    /// cross-company merge — the server still re-validates from its own data.
    public sealed record MergeTicketRequest(
        Guid TargetTicketId,
        bool AcknowledgedCrossCustomer);

    /// v0.0.23: payload for splitting a single received-mail off this ticket
    /// into a brand-new ticket with the same requester and system defaults
    /// for queue/priority/status. The agent fills the title manually.
    public sealed record SplitTicketRequest(
        long SourceMailEventId,
        string NewSubject);

    /// v0.0.9: per-company alert surfaced next to a ticket. Non-null only
    /// when the requester's contact is linked to an active company. The
    /// frontend decides whether to actually show a popup based on the
    /// alert_on_create / alert_on_open flags.
    public sealed record TicketCompanyAlert(
        Guid CompanyId,
        string CompanyName,
        string Code,
        string AlertText,
        bool AlertOnCreate,
        bool AlertOnOpen,
        string AlertOnOpenMode);
}
