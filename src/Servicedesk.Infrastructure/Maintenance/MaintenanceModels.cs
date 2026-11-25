namespace Servicedesk.Infrastructure.Maintenance;

/// v0.1.67 (Campaigns phase 3) — the Maintenance planner (ARCHITECTURE.md →
/// Maintenance planner): one plan = a recurring intervention for many
/// customers (members); every round mails each member from a NEW ticket via
/// the round's campaign. DATE columns travel as midnight DateTime values
/// (this Dapper version has no DateOnly map); they are server-local dates.
public static class MaintenanceScheduleModes
{
    /// After a round there is no next intervention until someone sets one.
    public const string Manual = "Manual";
    /// Fixed calendar: the next intervention = this one + the interval.
    public const string Automatic = "Automatic";

    public static bool IsValid(string? value) => value is Manual or Automatic;
}

public static class MaintenanceRoundStates
{
    public const string Pending = "Pending";
    public const string Created = "Created";
    public const string Failed = "Failed";
}

/// The fixed maintenance calendar (owner decision): the next intervention is
/// the previous one plus the interval. The anchor day keeps the day of month
/// across short months (31 Aug + 6 → 28 Feb → 31 Aug, never drifting).
public static class MaintenanceCalendar
{
    public static DateTime NextDue(DateTime due, int months, int anchorDay)
    {
        var next = due.Date.AddMonths(Math.Clamp(months, 1, 60));
        var day = Math.Min(Math.Clamp(anchorDay, 1, 31), DateTime.DaysInMonth(next.Year, next.Month));
        return new DateTime(next.Year, next.Month, day, 0, 0, 0, DateTimeKind.Unspecified);
    }

    /// The intervention moment: the date at the plan's time of day, in the
    /// server zone, as UTC. A wall time inside a DST gap moves an hour on.
    public static DateTime ProposedUtc(DateTime date, int minute, TimeZoneInfo zone)
    {
        var local = DateTime.SpecifyKind(date.Date.AddMinutes(Math.Clamp(minute, 0, 1439)), DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local)) local = local.AddHours(1);
        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }

    /// How long a ticket waits after an automatic mail: no pending status →
    /// none; no days → until the intervention moment; N days → N days after
    /// the mail, never past the intervention. An intervention already passed
    /// → none.
    public static DateTime? PendingTill(bool hasPendingStatus, int? pendingDays, DateTime interventionUtc, DateTime nowUtc)
    {
        if (!hasPendingStatus || interventionUtc <= nowUtc) return null;
        if (pendingDays is not { } days) return interventionUtc;
        var till = nowUtc.AddDays(days);
        return till < interventionUtc ? till : interventionUtc;
    }
}

public sealed record MaintenanceMemberInput(Guid? Id, Guid? CompanyId, Guid ContactId, string Label, bool IsActive, bool SkipNextRound = false);

public sealed record MaintenancePlanInput(
    string Name,
    string Notes,
    Guid QueueId,
    Guid? TicketTemplateId,
    string TicketSubject,
    string MailSubject,
    string MailBodyHtml,
    string ScheduleMode,
    int? IntervalMonths,
    DateTime? NextInterventionDate,
    int InterventionMinute,
    int MailDaysBefore,
    bool AutoSend,
    Guid? PendingStatusId,
    int? PendingDays,
    bool IsActive,
    IReadOnlyList<MaintenanceMemberInput> Members,
    int MailMinute = 540,
    MaintenanceReminderInput? Reminder = null,
    string OtherTimeNotice = "");

/// One reminder per round, N days after the first mail (auto-send plans only).
public sealed record MaintenanceReminderInput(bool Enabled, int Days, string Subject, string BodyHtml);

public sealed class MaintenanceMemberRow
{
    public Guid Id { get; set; }
    public Guid PlanId { get; set; }
    public Guid? CompanyId { get; set; }
    public string? CompanyName { get; set; }
    public Guid ContactId { get; set; }
    public string? ContactName { get; set; }
    public string? ContactEmail { get; set; }
    public string Label { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public bool SkipNextRound { get; set; }
    public int NoteCount { get; set; }
}

/// A plan as the planner list reads it, with its latest round's numbers.
public sealed class MaintenancePlanRow
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public Guid QueueId { get; set; }
    public string? QueueName { get; set; }
    public Guid? TicketTemplateId { get; set; }
    public string? TicketTemplateName { get; set; }
    public string TicketSubject { get; set; } = string.Empty;
    public string MailSubject { get; set; } = string.Empty;
    public string MailBodyHtml { get; set; } = string.Empty;
    public string ScheduleMode { get; set; } = MaintenanceScheduleModes.Manual;
    public int? IntervalMonths { get; set; }
    public DateTime? NextInterventionDate { get; set; }
    public int InterventionMinute { get; set; }
    public int MailMinute { get; set; }
    public int MailDaysBefore { get; set; }
    public bool AutoSend { get; set; }
    public Guid? SendAsUserId { get; set; }
    public string? SendAsEmail { get; set; }
    public Guid? PendingStatusId { get; set; }
    public int? PendingDays { get; set; }
    public bool IsActive { get; set; }
    public int MemberCount { get; set; }
    public Guid? LastRoundId { get; set; }
    public DateTime? LastInterventionDate { get; set; }
    public string? LastRoundState { get; set; }
    public string? LastRoundError { get; set; }
    public Guid? LastCampaignId { get; set; }
    public string? LastCampaignName { get; set; }
    public int LastTickets { get; set; }
    public int LastSent { get; set; }
    public int LastApproved { get; set; }
    public int LastOtherTime { get; set; }
    public int LastDeclined { get; set; }
    public int LastSkipped { get; set; }
    public DateTime? LastReminderUtc { get; set; }
    public string? LastReminderNote { get; set; }
    public string OtherTimeNotice { get; set; } = string.Empty;
    public bool ReminderEnabled { get; set; }
    public int ReminderDays { get; set; }
    public string ReminderSubject { get; set; } = string.Empty;
    public string ReminderBodyHtml { get; set; } = string.Empty;
    public DateTime UpdatedUtc { get; set; }
}

public sealed record MaintenancePlanDetail(MaintenancePlanRow Plan, IReadOnlyList<MaintenanceMemberRow> Members);
