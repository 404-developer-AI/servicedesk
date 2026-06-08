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
    string? RequestBodyText);
