namespace Servicedesk.Domain.Search;

/// One pluggable lookup for the global-search façade. Every new user-facing
/// entity (Companies, Kennisbank, …) is expected to ship an implementation
/// of this interface, including its own row-level authorization. The
/// façade never talks to data directly — it dispatches here.
public interface ISearchSource
{
    string Kind { get; }

    /// Whether this source has anything to contribute for the given
    /// principal. Settings returns false for non-Admins, Contacts returns
    /// false for Customers, etc. Used to hide tabs the user cannot use.
    bool IsAvailableFor(SearchPrincipal principal);

    Task<SearchGroup> SearchAsync(
        SearchRequest request,
        SearchPrincipal principal,
        CancellationToken ct);
}
