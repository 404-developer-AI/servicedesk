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
}

/// A single request to the search façade. <see cref="Type"/> is null for
/// the dropdown query (all sources, top-N each); non-null for the full
/// results page (single source, paginated).
public sealed record SearchRequest(
    string Query,
    string? Type,
    int Limit,
    int Offset);

/// Results grouped by source. The dropdown consumes all groups; the full
/// page consumes the single group matching <see cref="SearchRequest.Type"/>.
public sealed record SearchResults(
    IReadOnlyList<SearchGroup> Groups,
    int TotalHits);

public sealed record SearchGroup(
    string Kind,
    IReadOnlyList<SearchHit> Hits,
    int TotalInGroup,
    bool HasMore);

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
