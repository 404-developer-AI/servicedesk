using Servicedesk.Domain.Search;

namespace Servicedesk.Infrastructure.Search;

/// Dispatcher across every registered <see cref="ISearchSource"/>. The
/// dropdown path (Type == null) hits every source in parallel and takes
/// top-N from each; the full-page path (Type != null) runs a single
/// source, paginated.
public sealed class SearchService : ISearchService
{
    private readonly IReadOnlyList<ISearchSource> _sources;

    public SearchService(IEnumerable<ISearchSource> sources)
    {
        _sources = sources.ToList();
    }

    public async Task<SearchResults> SearchAsync(
        SearchRequest request, SearchPrincipal principal, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(principal);

        if (string.IsNullOrWhiteSpace(request.Query))
            return new SearchResults(Array.Empty<SearchGroup>(), 0);

        IEnumerable<ISearchSource> sources = _sources.Where(s => s.IsAvailableFor(principal));
        if (!string.IsNullOrWhiteSpace(request.Type))
            sources = sources.Where(s => string.Equals(s.Kind, request.Type, StringComparison.OrdinalIgnoreCase));

        var tasks = sources.Select(s => s.SearchAsync(request, principal, ct)).ToList();
        var groups = await Task.WhenAll(tasks);
        var total = groups.Sum(g => g.TotalInGroup);
        return new SearchResults(groups, total);
    }

    public IReadOnlyList<string> AvailableKindsFor(SearchPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return _sources.Where(s => s.IsAvailableFor(principal)).Select(s => s.Kind).ToList();
    }
}
