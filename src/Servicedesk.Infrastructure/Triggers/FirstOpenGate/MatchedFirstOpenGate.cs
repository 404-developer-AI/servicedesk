namespace Servicedesk.Infrastructure.Triggers.FirstOpenGate;

/// One first-open gate that matched a ticket the agent just opened.
/// Carries the dialog payload the UI renders for the blocking
/// title-review prompt plus the current subject (pre-filled into the
/// editable field) and the trigger id the confirmation echoes back.
public sealed record MatchedFirstOpenGate(
    Guid TriggerId,
    string Name,
    /// Dialog heading.
    string Title,
    /// Optional question shown above the editable field. Null when the
    /// admin disabled the message (field-only prompt).
    string? Message,
    /// Label rendered above the editable subject field. Falls back to a
    /// sensible default when the admin left it blank.
    string FieldLabel,
    /// The single approve button's label.
    string ConfirmLabel,
    /// The ticket's current subject, pre-filled into the editable field.
    string CurrentSubject,
    /// When true the dialog renders the original-request panel so the
    /// agent can read what the ticket is about before judging the title.
    bool ShowRequest,
    /// The ticket's original request body (the first message), HTML
    /// preferred, shown read-only above the title field. Null when the
    /// admin disabled the panel or the ticket has no body.
    string? RequestBodyHtml,
    /// Plain-text fallback for the original request when no HTML body
    /// exists. Null when HTML is present or the panel is disabled.
    string? RequestBodyText,
    /// True when the request panel content was sourced from the ticket's
    /// first inbound email rather than the ticket body — mail-created
    /// tickets keep their body empty, so the gate falls back to the mail.
    bool RequestFromMail,
    /// The mail_messages id of the first inbound email, set only when the
    /// panel content came from mail AND its raw .eml blob is available, so
    /// the dialog can offer a download of the original message.
    Guid? RequestMailId);
