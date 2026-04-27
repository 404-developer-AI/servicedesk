namespace Servicedesk.Infrastructure.Triggers;

/// Audit-event type strings emitted by the trigger pipeline. Kept here as
/// constants so the evaluator and the (future) admin CRUD endpoints both
/// reference the same canonical names — anyone grepping for "trigger." in
/// the audit log finds every entry.
public static class TriggerAuditEventTypes
{
    public const string Fired = "trigger.fired";
    public const string ActionApplied = "trigger.action_applied";
    public const string Created = "trigger.created";
    public const string Updated = "trigger.updated";
    public const string Deleted = "trigger.deleted";
}

/// Used as the <c>Actor</c> string when audit lines are written from the
/// trigger pipeline itself (not from a human's HTTP request). The matching
/// <c>ActorRole</c> is also <c>"system"</c>. Trigger-attribution lives in
/// the audit payload as <c>triggered_by</c> (the trigger id) rather than in
/// the Actor field, which is reserved for the user (or, here, the system).
public static class TriggerSystemActor
{
    public const string Name = "system";
    public const string Role = "system";
}
