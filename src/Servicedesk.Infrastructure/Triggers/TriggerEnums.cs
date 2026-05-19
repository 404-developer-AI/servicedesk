namespace Servicedesk.Infrastructure.Triggers;

public enum TriggerActivatorKind
{
    Action,
    Time,
    /// v0.0.39 — fires only when an agent explicitly invokes the trigger
    /// (e.g. clicking "Create linked order ticket" in the ticket side
    /// panel). Skipped by the event-driven evaluator and the time
    /// scheduler so the regular automation paths stay unchanged.
    Manual,
}

public enum TriggerActivatorMode
{
    Selective,
    Always,
    Reminder,
    Escalation,
    EscalationWarning,
}

public enum TriggerRunOutcome
{
    Applied,
    SkippedNoMatch,
    SkippedLoop,
    Failed,
}
