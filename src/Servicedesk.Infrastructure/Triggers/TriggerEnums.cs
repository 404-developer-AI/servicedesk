namespace Servicedesk.Infrastructure.Triggers;

public enum TriggerActivatorKind
{
    Action,
    Time,
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
