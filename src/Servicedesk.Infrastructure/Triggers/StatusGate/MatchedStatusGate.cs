namespace Servicedesk.Infrastructure.Triggers.StatusGate;

/// One gate that matched a pending agent-initiated status change. Carries
/// the dialog payload the UI needs to render the confirmation plus the
/// trigger id the confirmation must echo back. The note template is
/// unrendered — the caller passes it to
/// <see cref="IStatusGateService.RenderNoteAsync"/> once the agent submits
/// their answers so token substitution sees the final values.
///
/// v0.0.42 ships two gate kinds:
///   * <c>prompt_confirm</c> — multi-question confirmation dialog
///     (free-text + yes/no rows). <see cref="Questions"/> is populated;
///     <see cref="RequesterContactId"/> / <see cref="RequesterDisplayName"/>
///     are null.
///   * <c>contact_company_link</c> — hard-block dialog when the ticket's
///     requester has no <c>contact_companies</c> row. <see cref="Questions"/>
///     is empty; the requester fields are populated so the dialog can
///     render an inline company-picker + role selector. Confirmation goes
///     through the same PATCH endpoint with a kind-specific payload.
public sealed record MatchedStatusGate(
    Guid TriggerId,
    string Name,
    /// "prompt_confirm" or "contact_company_link". Drives both the FE
    /// dialog discrimination and the BE confirmation-payload check.
    string Kind,
    string Title,
    /// Optional message body shown above the questions / picker. Null
    /// when the admin disabled the message (button-only gate).
    /// Whitespace-only strings are projected as null so the dialog skips
    /// the section.
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
    Guid? FromStatusId,
    /// Populated only for <c>contact_company_link</c> gates. Carries the
    /// ticket's requester so the dialog can call POST
    /// /api/contacts/{id}/companies via the confirmation flow.
    Guid? RequesterContactId,
    /// Friendly display string for the requester (first+last name with
    /// email fallback). Rendered in the dialog so the agent knows which
    /// contact they're linking. Null when not a contact-company gate.
    string? RequesterDisplayName);

/// One question rendered inline in the confirmation dialog. <c>Key</c>
/// drives the <c>#{prompt.&lt;key&gt;}</c> token in the note template.
/// <c>Type</c> is "text" (free-text textarea), "yesno" (two button slots,
/// either side can be hidden via null labels), or "choice" (v0.0.89 — a
/// single-select list of options, each tagged with an outcome that decides
/// whether the status change may proceed).
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
    string? NoLabel,
    /// Populated only when <c>Type == "choice"</c>. The agent must pick
    /// exactly one option; the picked option's <see cref="GateChoiceOption.Outcome"/>
    /// decides whether the status change proceeds (allow) or is held back
    /// (keep_open). Null/empty for text + yesno questions.
    IReadOnlyList<GateChoiceOption>? Options = null);

/// One selectable option on a <c>choice</c> question. <see cref="Outcome"/>
/// is the server-trusted effect of picking it — never read from the client:
///   * <c>"allow"</c> — the status change proceeds.
///   * <c>"keep_open"</c> — the status change is held back; the ticket
///     keeps its current status. The decision is still logged.
public sealed record GateChoiceOption(string Label, string Outcome);
