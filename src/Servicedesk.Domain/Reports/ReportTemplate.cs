namespace Servicedesk.Domain.Reports;

/// A reusable report email template (Contracts → Settings → Email templates).
/// <see cref="Purpose"/> discriminates report kinds — only "m365" exists today
/// (the Microsoft 365 matching overview), but the column keeps the door open
/// for future kinds and the planned bulk-send screen.
/// <see cref="QueueId"/> chooses the FROM mailbox (the queue's
/// outbound/inbound address) so the sender is fixed at authoring time.
/// <see cref="Columns"/> is the default column selection for the overview;
/// <see cref="Scope"/> is "all" or "unprotected".
public sealed record ReportTemplate(
    Guid Id,
    string Purpose,
    string Name,
    string? Description,
    string Subject,
    string BodyHtml,
    Guid? QueueId,
    IReadOnlyList<string> Columns,
    string Scope,
    bool AttachPdf,
    bool IsActive,
    DateTime CreatedUtc,
    DateTime UpdatedUtc,
    Guid? CreatedBy);

/// Allowed values for <see cref="ReportTemplate.Scope"/>.
public static class ReportScope
{
    /// Every mailbox is listed in the overview.
    public const string All = "all";

    /// Only mailboxes that are unprotected on at least one configured axis
    /// (spam / OneDrive / Exchange) — the "action needed" view.
    public const string Unprotected = "unprotected";

    public static bool IsValid(string? value) => value is All or Unprotected;
}

/// Report purpose discriminators.
public static class ReportPurpose
{
    public const string M365 = "m365";
}
