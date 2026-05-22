namespace Servicedesk.Infrastructure.Triggers.StatusGate;

/// One gate that matched a pending agent-initiated status change. Carries
/// the prompt payload the dialog needs to render plus the trigger id the
/// confirmation must echo back. The note template is unrendered — the
/// caller passes it to <see cref="IStatusGateService.RenderNoteAsync"/>
/// once the agent submits their answers so token substitution sees the
/// final values.
public sealed record MatchedStatusGate(
    Guid TriggerId,
    string Name,
    string Title,
    /// Optional message body shown above the questions. Null when the
    /// admin disabled the message (button-only gate). Whitespace-only
    /// strings are projected as null so the dialog skips the section.
    string? Message,
    IReadOnlyList<GateQuestion> Questions,
    string ConfirmLabel,
    string CancelLabel,
    /// "internal" or "public" — drives is_internal on the appended note.
    string NoteVisibility,
    /// May be empty; renderer returns empty string and the endpoint
    /// skips note insertion to avoid an empty timeline entry.
    string NoteTemplate,
    Guid ToStatusId,
    Guid? FromStatusId);

/// One question rendered inline in the confirmation dialog. <c>Key</c>
/// drives the <c>#{prompt.&lt;key&gt;}</c> token in the note template.
/// <c>Type</c> is either "text" (free-text textarea) or "yesno"
/// (two button slots, either side can be hidden via null labels).
public sealed record GateQuestion(
    string Key,
    string Type,
    string Label,
    /// Only meaningful when <c>Type == "text"</c>. Yes/no answers are
    /// always "required" in a different sense — the agent must click a
    /// visible button — so the flag is ignored for that variant.
    bool Required,
    /// "Yes" button label. Null = the button is hidden; the question
    /// then offers no positive-confirmation path and the gate can only
    /// be cancelled through it.
    string? YesLabel,
    /// "No" button label. Null = the button is hidden; the question
    /// then offers no cancel path through it (cancel via the dialog's
    /// own Cancel button or overlay/Esc).
    string? NoLabel);
