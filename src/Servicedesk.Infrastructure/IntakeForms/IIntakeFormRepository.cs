using System.Text.Json;
using Servicedesk.Domain.IntakeForms;

namespace Servicedesk.Infrastructure.IntakeForms;

/// Per-ticket instance operations. Each state transition (Draft → Sent,
/// Sent → Submitted, Sent → Expired) is atomic: the status update and the
/// matching ticket_events row are written in the same transaction so there
/// is never a visible gap where a form is Sent without its timeline entry.
public interface IIntakeFormRepository
{
    Task<Guid> CreateDraftAsync(Guid ticketId, Guid templateId, string prefillJson, Guid? createdBy, CancellationToken ct);

    /// Updates a Draft's prefill JSON. Returns false when the instance is not
    /// found, doesn't belong to this ticket, or is no longer Draft.
    Task<bool> UpdateDraftPrefillAsync(Guid instanceId, Guid ticketId, string prefillJson, CancellationToken ct);

    /// Hard-deletes a Draft that never left the server. Returns false when
    /// the row is not Draft or doesn't belong to this ticket.
    Task<bool> DeleteDraftAsync(Guid instanceId, Guid ticketId, CancellationToken ct);

    /// Lightweight per-ticket listing for the timeline/drawer UX.
    Task<IReadOnlyList<IntakeFormInstanceSummary>> ListForTicketAsync(Guid ticketId, CancellationToken ct);

    /// Full agent-facing view: instance + its (live) template + questions +
    /// options. Used by the prefill drawer and the resend confirmation.
    Task<IntakeFormAgentView?> GetAgentViewAsync(Guid ticketId, Guid instanceId, CancellationToken ct);

    /// Atomic Draft → Sent + IntakeFormSent event. Returns the sent_event_id
    /// on success, or null when the row is not Draft / doesn't belong to the
    /// ticket / template is inactive. Raw token never enters this method —
    /// the caller generates it, hashes it, and passes the hash + cipher.
    Task<long?> SendDraftAsync(
        Guid instanceId,
        Guid ticketId,
        Guid actorUserId,
        byte[] tokenHash,
        byte[] tokenCipher,
        DateTime expiresUtc,
        string sentToEmail,
        string metadataJson,
        CancellationToken ct);

    /// Cancels a Sent instance (used by the resend flow to retire the old
    /// link before minting a new one). Returns the ticketId on success so the
    /// caller can broadcast a refresh.
    Task<bool> CancelSentAsync(Guid instanceId, Guid ticketId, Guid actorUserId, CancellationToken ct);

    /// Public path. Looks up by sha256(token). Returns null for unknown
    /// tokens AND for Draft/Cancelled rows — the caller should 404 in both
    /// cases to avoid leaking whether a token ever existed.
    Task<IntakePublicView?> GetByTokenHashForPublicAsync(byte[] tokenHash, CancellationToken ct);

    /// Atomic submit: checks status=Sent + not-expired, writes answers +
    /// IntakeFormSubmitted event, flips status. When <paramref name="autoPin"/>
    /// is true, also inserts a ticket_event_pins row attributing the pin to
    /// the agent who originally sent the form, all in the same transaction —
    /// so the submission lands pre-pinned without a follow-up request.
    /// Returns the ticketId + submittedEventId on success. On 409 (already
    /// submitted, cancelled, or expired between GET and POST) returns null.
    Task<SubmitResult?> TrySubmitAsync(
        byte[] tokenHash,
        IReadOnlyList<IntakeFormSubmitAnswer> answers,
        string? ip,
        string? userAgent,
        DateTime nowUtc,
        bool autoPin,
        CancellationToken ct);

    /// Flips Sent → Expired for all past-due rows and writes an
    /// IntakeFormExpired event per ticket. Returns the touched ids so the
    /// caller can broadcast SignalR invalidations.
    Task<IReadOnlyList<ExpiredInstance>> ExpireStaleAsync(int maxBatch, DateTime nowUtc, CancellationToken ct);
}

public sealed record IntakeFormInstanceSummary(
    Guid Id,
    Guid TemplateId,
    string TemplateName,
    IntakeFormStatus Status,
    DateTime? ExpiresUtc,
    DateTime CreatedUtc,
    DateTime? SentUtc,
    DateTime? SubmittedUtc,
    string? SentToEmail);

public sealed record IntakeFormAgentView(
    IntakeFormInstance Instance,
    IntakeTemplate Template,
    /// Populated when <see cref="IntakeFormInstance.Status"/> is Submitted:
    /// a JSON object keyed by questionId-as-string with the customer's
    /// answer. Null for Draft/Sent/Expired/Cancelled so the agent UI can
    /// distinguish "nothing submitted yet" from "submitted with empty
    /// optional fields".
    JsonDocument? Answers = null);

public sealed record IntakePublicView(
    Guid InstanceId,
    Guid TemplateId,
    string TemplateName,
    string? TemplateDescription,
    IntakeFormStatus Status,
    DateTime? ExpiresUtc,
    JsonDocument Prefill,
    IReadOnlyList<IntakeQuestion> Questions);

public sealed record IntakeFormSubmitAnswer(long QuestionId, string AnswerJson);

public sealed record SubmitResult(
    Guid InstanceId,
    Guid TicketId,
    Guid TemplateId,
    string TemplateName,
    long SubmittedEventId);

public sealed record ExpiredInstance(Guid InstanceId, Guid TicketId, Guid TemplateId, string TemplateName, long ExpiredEventId);
