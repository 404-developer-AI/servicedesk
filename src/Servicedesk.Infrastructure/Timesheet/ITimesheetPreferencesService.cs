namespace Servicedesk.Infrastructure.Timesheet;

/// v0.0.35-E — resolves "effective" Timesheet preferences for a user.
///
/// The user-row carries optional override columns (`timesheet_*`). When
/// they are NULL, the global default from the <c>settings</c> table wins.
/// Callers always get a fully-populated <see cref="TimesheetPreferences"/>
/// so Tab-1 and Tab-3 never have to handle nulls.
///
/// The override mutation API is here too so the user-edit page can write
/// new override values (or NULL them out) without going through the
/// general settings endpoint.
public interface ITimesheetPreferencesService
{
    /// Returns the user's effective preferences. Reads the user row +
    /// global defaults, merges per field. Never returns null — a missing
    /// user yields globals-only.
    Task<TimesheetPreferences> GetEffectiveAsync(Guid userId, CancellationToken ct = default);

    /// Returns the raw override values stored on the user row (with
    /// `null` for "no override"). Used by the admin override dialog so
    /// the form shows empty fields when the user is on the global
    /// default.
    Task<TimesheetOverride?> GetOverrideAsync(Guid userId, CancellationToken ct = default);

    /// Writes the user's overrides. Any field set to <c>null</c> in
    /// <paramref name="input"/> clears that override (so the user falls
    /// back to the global default for that field). Validation: minutes
    /// stay in their allowed ranges; the work-days CSV is normalised
    /// and validated server-side. The returned <see cref="TimesheetOverride"/>
    /// reflects what was actually persisted.
    Task<UpdateOverrideResult> UpdateOverrideAsync(
        Guid userId, TimesheetOverrideInput input, CancellationToken ct = default);

    /// Self-service write of the caller's own default Tab-1 task. A non-null
    /// <paramref name="taskId"/> must reference an existing, non-archived task;
    /// <c>null</c> clears the preference (UI falls back to the first active
    /// task). Unlike <see cref="UpdateOverrideAsync"/> this is agent-reachable
    /// for their own row, not an admin-only override.
    Task<UpdateDefaultTaskResult> UpdateDefaultTaskAsync(
        Guid userId, Guid? taskId, CancellationToken ct = default);
}

/// The full effective preference bundle. All fields are non-nullable —
/// the merge already happened.
public sealed record TimesheetPreferences(
    int DayStartMinutes,
    int TargetMinutesPerDay,
    int TargetMinutesPerWeek,
    /// ISO weekday numbers (1=Mon..7=Sun). Ordered ascending, no
    /// duplicates.
    IReadOnlyList<int> WorkDays,
    /// v0.0.36 — daily ceiling on absence-task minutes before the week
    /// is flagged "target not met". 0 = no ceiling.
    int MaxAbsenceMinutesPerDay,
    /// v0.0.36 — office-hour window for the Tab 1 row-to-row gap/overlap
    /// check. A mismatch only flags red when its zone intersects this
    /// window. Both are minutes-since-midnight (0..1440).
    int OfficeStartMinutes,
    int OfficeEndMinutes,
    /// v0.0.74 — the user's personal default task for new Tab-1 rows.
    /// `null` means "no preference"; the client then seeds a new row with
    /// the first active task (sort order). Purely per-user; there is no
    /// global default-task setting — the implicit global is "first active".
    Guid? DefaultTaskId);

/// What the user has actually overridden. Each field is independently
/// nullable so an admin can override only the day-start and let the
/// targets stay on the globals.
public sealed record TimesheetOverride(
    int? DayStartMinutes,
    int? TargetMinutesPerDay,
    int? TargetMinutesPerWeek,
    /// `null` means "no override". An empty list (`[]`) is also valid —
    /// it explicitly says "this user has no working days" which is
    /// different from "use the default".
    IReadOnlyList<int>? WorkDays,
    int? MaxAbsenceMinutesPerDay,
    int? OfficeStartMinutes,
    int? OfficeEndMinutes);

/// Input for the admin override mutation. Same shape as
/// <see cref="TimesheetOverride"/>; nulls clear the override.
public sealed record TimesheetOverrideInput(
    int? DayStartMinutes,
    int? TargetMinutesPerDay,
    int? TargetMinutesPerWeek,
    IReadOnlyList<int>? WorkDays,
    int? MaxAbsenceMinutesPerDay,
    int? OfficeStartMinutes,
    int? OfficeEndMinutes);

public abstract record UpdateOverrideResult
{
    public sealed record Updated(TimesheetOverride Override) : UpdateOverrideResult;
    public sealed record UserNotFound : UpdateOverrideResult;
    public sealed record ValidationFailed(IReadOnlyList<TimesheetFieldError> Errors) : UpdateOverrideResult;
}

public abstract record UpdateDefaultTaskResult
{
    /// <paramref name="TaskId"/> echoes what was persisted (null = cleared).
    public sealed record Updated(Guid? TaskId) : UpdateDefaultTaskResult;
    public sealed record UserNotFound : UpdateDefaultTaskResult;
    /// The requested task does not exist or is archived.
    public sealed record TaskNotFound : UpdateDefaultTaskResult;
}
