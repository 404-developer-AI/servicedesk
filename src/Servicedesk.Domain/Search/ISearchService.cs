namespace Servicedesk.Domain.Search;

/// Entry point for every search query — dropdown and full page both go
/// through here. The <paramref name="principal"/> parameter is required
/// so that no code path can accidentally run an unscoped query.
public interface ISearchService
{
    Task<SearchResults> SearchAsync(
        SearchRequest request,
        SearchPrincipal principal,
        CancellationToken ct);

    /// Source kinds this principal may see results for. Powers the tab
    /// visibility on the full-search page and the "Toon zoekdetails"
    /// deeplink (must default to a tab the user can actually see).
    IReadOnlyList<string> AvailableKindsFor(SearchPrincipal principal);
}
