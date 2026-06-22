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
}
