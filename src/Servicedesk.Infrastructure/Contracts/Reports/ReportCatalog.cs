namespace Servicedesk.Infrastructure.Contracts.Reports;

/// One selectable column in the Microsoft 365 report overview. <see cref="Key"/>
/// is the stable identifier stored on the template + send log and sent over the
/// wire; <see cref="Label"/> is the human header rendered in the email/PDF.
public sealed record ReportColumn(string Key, string Label);

/// The canonical catalogue of Microsoft 365 report columns. The order here is
/// the order columns render in — a requested selection is intersected with this
/// list and rendered in this order, so the table layout is consistent
/// regardless of the order the user ticked the boxes.
public static class ReportColumns
{
    public const string Type = "type";
    public const string Name = "name";
    public const string Upn = "upn";
    public const string Mail = "mail";
    public const string Enabled = "enabled";
    public const string Licenses = "licenses";
    public const string Spam = "spam";
    public const string OneDrive = "onedrive";
    public const string Exchange = "exchange";

    public static readonly IReadOnlyList<ReportColumn> All = new[]
    {
        new ReportColumn(Type, "Type"),
        new ReportColumn(Name, "Name"),
        new ReportColumn(Upn, "UPN"),
        new ReportColumn(Mail, "Email"),
        new ReportColumn(Enabled, "Enabled"),
        new ReportColumn(Licenses, "Licenses"),
        new ReportColumn(Spam, "Spam filter"),
        new ReportColumn(OneDrive, "OneDrive backup"),
        new ReportColumn(Exchange, "Exchange backup"),
    };

    private static readonly HashSet<string> Known =
        All.Select(c => c.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static bool IsValid(string key) => Known.Contains(key);

    /// Normalises a requested selection: drops unknown/blank keys, de-dupes,
    /// and returns the survivors in canonical (catalogue) order. Falls back to
    /// a sensible default when the request is empty so a report is never blank.
    public static IReadOnlyList<string> Normalize(IEnumerable<string>? requested)
    {
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (requested is not null)
        {
            foreach (var k in requested)
            {
                if (!string.IsNullOrWhiteSpace(k) && Known.Contains(k.Trim()))
                    wanted.Add(k.Trim());
            }
        }
        var ordered = All.Where(c => wanted.Contains(c.Key)).Select(c => c.Key).ToList();
        return ordered.Count > 0 ? ordered : Default;
    }

    /// Used only when normalisation finds nothing usable. The configured
    /// factory default lives in settings (Contracts.Reports.DefaultColumns);
    /// this is the last-resort fallback if that is also empty/invalid.
    public static readonly IReadOnlyList<string> Default = new[] { Type, Name, Upn, Spam, OneDrive, Exchange };

    public static IReadOnlyList<string> ParseCsv(string? csv) =>
        Normalize(csv?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}

/// One placeholder the report template body/subject can carry. <see cref="Token"/>
/// is the literal text (incl. braces) the author types; the renderer substitutes
/// it before send. <see cref="Label"/> drives the "insert variable" picker.
public sealed record ReportToken(string Token, string Label);

/// The placeholders supported in a Microsoft 365 report template. {{report.table}}
/// is special — it is replaced by the generated HTML overview, not a scalar.
public static class ReportTokens
{
    public const string Table = "{{report.table}}";

    public static readonly IReadOnlyList<ReportToken> Supported = new[]
    {
        new ReportToken("{{company.name}}", "Company name"),
        new ReportToken("{{company.code}}", "Company code"),
        new ReportToken("{{report.date}}", "Report date"),
        new ReportToken("{{report.mailboxCount}}", "Mailbox count"),
        new ReportToken("{{report.spamProtected}}", "Spam-protected count"),
        new ReportToken("{{report.onedriveProtected}}", "OneDrive-protected count"),
        new ReportToken("{{report.exchangeProtected}}", "Exchange-protected count"),
        new ReportToken(Table, "Overview table"),
        new ReportToken("{{user.name}}", "Your name"),
        new ReportToken("{{user.email}}", "Your email"),
    };
}
