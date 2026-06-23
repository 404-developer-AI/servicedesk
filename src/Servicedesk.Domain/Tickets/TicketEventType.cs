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
}
