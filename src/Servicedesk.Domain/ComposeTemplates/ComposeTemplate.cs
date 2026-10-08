namespace Servicedesk.Domain.ComposeTemplates;

public sealed record ComposeTemplate(
    Guid Id,
    string Name,
    string? Description,
    string BodyHtml,
    bool IsActive,
    // Empty = available in every queue; otherwise restricted to listed queue ids.
    IReadOnlyList<Guid> QueueIds,
    DateTime CreatedUtc,
    DateTime UpdatedUtc,
    Guid? CreatedBy,
    // v0.0.38 — optional CSAT survey to dispatch when an agent sends a
    // reply/note built from this template. Null = no survey-on-send.
    Guid? LinkedSurveyId = null,
    // v0.0.42 — multi-status scope (empty = any). ANDed with QueueIds when
    // the picker / auto-insert matcher resolves a candidate template.
    IReadOnlyList<Guid>? StatusIds = null,
    // v0.0.42 — when true, the template is auto-prefilled into the
    // "Write an internal note" composer whenever the agent opens an empty
    // composer on a ticket matching this template's queue/status scope.
    bool AutoInsertOnNote = false,
    // v0.1.17 — per-kind scope for the split composer (Note / Mail / Call)
    // and auto-insert into an empty Call composer. Kinds default on so
    // pre-v0.1.17 templates keep showing everywhere.
    bool UseForNote = true,
    bool UseForMail = true,
    bool UseForCall = true,
    bool AutoInsertOnCall = false,
    // v0.1.32 — Insights Workflow: the template counts as the closing
    // "last action" before Pending, and/or as a Specialist Consult.
    bool WorkflowClose = false,
    bool SpecialistConsult = false);

/// v0.1.17 — the composer a template is offered in.
public enum ComposeTemplateKind
{
    Note,
    Mail,
    Call,
}

/// v0.1.17 — per-kind settings carried through create/update.
public sealed record ComposeTemplateKindScope(
    bool UseForNote,
    bool UseForMail,
    bool UseForCall,
    bool AutoInsertOnCall);
