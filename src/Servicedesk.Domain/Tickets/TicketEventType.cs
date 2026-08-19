namespace Servicedesk.Domain.Tickets;

public enum TicketEventType
{
    Created,
    Comment,
    Mail,
    Note,
    StatusChange,
    AssignmentChange,
    PriorityChange,
    QueueChange,
    CategoryChange,
    SystemNote,
    MailReceived,
    MailSent,
    IntakeFormSent,
    IntakeFormSubmitted,
    IntakeFormExpired,
    SurveySent,
    SurveySubmitted,
    SurveyExpired,
    // v0.0.87 — per-ticket hour-limit alert outcomes.
    TimeLimitAlertDismissed,
    TimeLimitExtended,
    // v0.0.89 — status-change gate decision (the agent's chosen option on
    // a prompt_confirm choice question, logged whether the ticket was
    // allowed to change status or kept open).
    StatusGateDecision,
    // v0.0.103 — ticket checklists: attach / detach / all required items
    // done / reopened after completion, and the opt-in per-item change.
    ChecklistAttached,
    ChecklistDetached,
    ChecklistCompleted,
    ChecklistReopened,
    ChecklistItemChanged,
    // A trigger's set-status action was refused by the checklist close block.
    ChecklistCloseBlocked,
    // v0.0.104 — project tickets: convert / revert on the project ticket,
    // link / unlink on the ticket that joins or leaves a project.
    ProjectConverted,
    ProjectReverted,
    ProjectLinked,
    ProjectUnlinked,
}
