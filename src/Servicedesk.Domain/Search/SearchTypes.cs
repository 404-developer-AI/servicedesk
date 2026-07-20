namespace Servicedesk.Domain.Search;

/// Identifiers for each search source. Kept as constants (not an enum) so
/// the strings travel unchanged across the API boundary and URL search
/// params.
public static class SearchSourceKind
{
    public const string Tickets = "tickets";
    public const string Contacts = "contacts";
    public const string Companies = "companies";
    public const string Settings = "settings";
    public const string IntakeTemplates = "intake-templates";
    public const string IntakeSubmissions = "intake-submissions";
    public const string Triggers = "triggers";
    public const string KbArticles = "kb-articles";
    public const string Timesheet = "timesheet";
    public const string Surveys = "surveys";
    public const string Assets = "assets";
    public const string AdsolutSalesReceipts = "adsolut-sales-receipts";
    public const string AdsolutOrders = "adsolut-orders";
    public const string AdsolutArticles = "adsolut-articles";
    public const string AdsolutContracts = "adsolut-contracts";
    public const string M365Mailboxes = "m365-mailboxes";
    public const string SophosTenants = "sophos-tenants";
    public const string Signatures = "signatures";
    public const string ReportTemplates = "report-templates";
    public const string EmployeeFeedback = "employee-feedback";
}

/// Per-user feature flags that gate availability of certain search sources.
/// Loaded once into the <see cref="SearchPrincipal"/> so a source can decide
/// in <c>IsAvailableFor</c> (synchronously, no DB) whether the user may use
/// it — keeping sources the user has no access to out of the search dropdown
/// entirely, instead of showing an empty tab. Customers never reach these.
public static class SearchFeature
{
    /// Gates the Adsolut/Wolters-Kluwer "Contracts" family: contracts,
    /// articles, report templates, and the M365/Sophos protection sources.
    public const string Contracts = "contracts";

    /// Gates the Adsolut Orders source.
    public const string AdsolutOrders = "adsolutOrders";

    /// Gates the Adsolut sales-receipts (mirrored timesheet) source.
    public const string AdsolutTimesheet = "adsolutTimesheet";

    /// Gates the Employee Feedback board source for FULL-access users
    /// (feedback_enabled) — every row is searchable.
    public const string Feedback = "feedback";

    /// v0.0.90 — gates the Employee Feedback source for RESTRICTED users
    /// (feedback_own_only): the source is available but
    /// <see cref="Servicedesk.Domain.Search"/> consumers scope results to the
    /// user's own rows. Mutually exclusive with <see cref="Feedback"/> — a
    /// principal never carries both (full wins at flag-resolution time).
    public const string FeedbackOwnOnly = "feedbackOwnOnly";
}

/// Sort order for the full-page search. Parsed from the `sort` query param
/// (Relevance is the fallback for unknown values). Every source maps it onto
/// a fixed set of ORDER BY constants — never interpolated user input. Sources
/// without a meaningful timestamp simply ignore non-Relevance values;
/// <see cref="Status"/> is only meaningful for the tickets source (open
/// before closed).
public enum SearchSort
{
    Relevance,
    Newest,
    Oldest,
    Status,
}

/// A single request to the search façade. <see cref="Type"/> is null for
/// the dropdown query (top-N each); non-null for the full results page
/// (single source, paginated).
///
/// <see cref="Kinds"/> scopes the dropdown to the sources configured in
/// Search.QuickSources (null = no restriction). <see cref="QuickMode"/> tells
/// a source it is serving the dropdown — the tickets source then partitions
/// its top-N into open and closed tickets instead of one flat list.
public sealed record SearchRequest(
    string Query,
    string? Type,
    int Limit,
    int Offset,
    IReadOnlyList<string>? Kinds = null,
    bool QuickMode = false,
    SearchSort Sort = SearchSort.Relevance);

/// Results grouped by source. The dropdown consumes all groups; the full
/// page consumes the single group matching <see cref="SearchRequest.Type"/>.
public sealed record SearchResults(
    IReadOnlyList<SearchGroup> Groups,
    int TotalHits);

/// <see cref="PartitionTotals"/> is only set by the tickets source in quick
/// mode: total match counts per partition ("open"/"closed") so the dropdown
/// can render a "+N more" hint per section. Null everywhere else.
public sealed record SearchGroup(
    string Kind,
    IReadOnlyList<SearchHit> Hits,
    int TotalInGroup,
    bool HasMore,
    IReadOnlyDictionary<string, int>? PartitionTotals = null);

/// One hit in the result set. <see cref="Kind"/> lets the UI pick the
/// right icon/route; <see cref="EntityId"/> is the clickable target.
/// <see cref="Snippet"/> is pre-rendered on the server with match markers.
public sealed record SearchHit(
    string Kind,
    string EntityId,
    string Title,
    string? Snippet,
    double Rank,
    IReadOnlyDictionary<string, string?>? Meta = null);
