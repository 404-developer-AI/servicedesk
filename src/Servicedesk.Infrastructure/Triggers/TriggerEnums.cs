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
    /// v0.0.42 — interactive gate. Evaluated synchronously by the ticket
    /// PATCH endpoint before applying a mutation; surfaces a confirmation
    /// dialog in the agent UI. Never fired by the event evaluator or the
    /// time scheduler — automation paths bypass entirely.
    Gate,
}

public enum TriggerActivatorMode
{
    Selective,
    Always,
    Reminder,
    Escalation,
    EscalationWarning,
    /// v0.0.42 — paired with <see cref="TriggerActivatorKind.Gate"/>. The
    /// single mode for now; future gate modes (e.g. priority-change,
    /// queue-change) can be added in the same slot.
    StatusChange,
}

public enum TriggerRunOutcome
{
    Applied,
    SkippedNoMatch,
    SkippedLoop,
    Failed,
}
