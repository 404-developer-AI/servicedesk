namespace Servicedesk.Infrastructure.Auth;

/// Effective Employee Feedback access scope for a user. <c>Full</c> = the
/// shared board (see + CRUD every row); <c>OwnOnly</c> = may log feedback but
/// only see/edit rows they created; <c>None</c> = no access.
public enum FeedbackAccess
{
    None = 0,
    OwnOnly = 1,
    Full = 2,
}

/// Raw Employee Feedback flags + the resolved <see cref="FeedbackAccess"/>.
/// Full (feedback_enabled) takes precedence over OwnOnly (feedback_own_only)
/// when both are set, so the own-only flag is purely additive — it can only
/// grant access, never narrow a full user.
public readonly record struct FeedbackAccessInfo(bool Enabled, bool OwnOnly)
{
    public FeedbackAccess Access =>
        Enabled ? FeedbackAccess.Full
        : OwnOnly ? FeedbackAccess.OwnOnly
        : FeedbackAccess.None;
}

/// Per-user Timesheet feature flags. Empty struct-y record so the call
/// stays cheap and we don't allocate a class for two booleans.
/// v0.1.24 — all per-user feature flags, as read in one query for /api/auth/me.
public sealed record UserFeatureFlags(
    TimesheetFlags Timesheet,
    IsoFlags Iso,
    bool KbEnabled,
    bool SearchEnabled,
    bool ActivityFeedEnabled,
    bool AssetsEnabled,
    bool AdsolutTimesheetEnabled,
    bool AdsolutOrdersEnabled,
    bool TimesheetBackofficeEnabled,
    bool BackofficeFormsEnabled,
    bool StatisticsRead,
    bool StatisticsWrite,
    bool ContractsEnabled,
    bool InsightsEnabled,
    FeedbackAccessInfo Feedback)
{
    /// v0.1.59 — Order Management (TD SYNNEX orders).
    public bool OrderManagementEnabled { get; init; }

    /// v0.1.61 — "Plan in calendars".
    public bool CalendarPlanningEnabled { get; init; }

    /// v0.1.67 — Campaigns (bulk mail through the campaign outbox).
    public bool CampaignsEnabled { get; init; }
}

public readonly record struct TimesheetFlags(bool Enabled, bool Manager)
{
    public static TimesheetFlags None => default;
}

/// Per-user ISO 27001 workflow flags. Mgm = first-line reviewer; Dpo =
/// data-protection officer who registers incidents in the ISMS. A user
/// can carry both (admins often do) or neither (default for new users).
public readonly record struct IsoFlags(bool Mgm, bool Dpo)
{
    public static IsoFlags None => default;
}
