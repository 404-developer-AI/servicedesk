using Servicedesk.Domain.Search;

namespace Servicedesk.Infrastructure.Search;

/// Defense-in-depth wrapper. Every source is registered behind this
/// decorator: it re-checks availability for the principal before handing
/// control to the inner source. If a future refactor accidentally removes
/// the availability check inside a source, the wrapper still returns an
/// empty group instead of leaking results.
public sealed class ScopedSearchSource : ISearchSource
{
    private readonly ISearchSource _inner;

    public ScopedSearchSource(ISearchSource inner) => _inner = inner;

    public string Kind => _inner.Kind;

    public bool IsAvailableFor(SearchPrincipal principal) => _inner.IsAvailableFor(principal);

    public Task<SearchGroup> SearchAsync(SearchRequest request, SearchPrincipal principal, CancellationToken ct)
    {
        if (principal is null)
            throw new ArgumentNullException(nameof(principal));
        if (!_inner.IsAvailableFor(principal))
            return Task.FromResult(new SearchGroup(_inner.Kind, Array.Empty<SearchHit>(), 0, false));
        return _inner.SearchAsync(request, principal, ct);
    }
}
