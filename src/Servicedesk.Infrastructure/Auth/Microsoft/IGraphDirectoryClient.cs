namespace Servicedesk.Infrastructure.Auth.Microsoft;

/// Read-only Microsoft Graph access for directory objects (Azure AD users).
/// Separated from <c>IGraphMailClient</c> because the two serve different
/// concerns and typically carry different permission scopes —
/// <c>Mail.ReadWrite</c> / <c>Mail.Send</c> on the mail side,
/// <c>User.Read.All</c> on this side. Both currently share one app
/// registration and one <c>GraphClientSecret</c>; if a future install ever
/// needs distinct registrations, this is where the split would land.
public interface IGraphDirectoryClient
{
    /// Reads <c>accountEnabled</c> for the Azure AD user identified by
    /// <paramref name="oid"/>. Returns <c>null</c> if the user no longer
    /// exists in the tenant (deleted, or was never in this tenant to begin
    /// with). The M365 login callback treats both <c>false</c> and <c>null</c>
    /// as "cannot login" — the distinction only matters for the audit
    /// payload.
    /// </summary>
    Task<GraphUserStatus?> GetUserStatusAsync(string oid, CancellationToken ct = default);

    /// Typeahead search for the M365 add-user picker. Matches against
    /// <c>displayName</c> / <c>userPrincipalName</c> / <c>mail</c> via Graph's
    /// <c>$search</c> parameter (requires the <c>ConsistencyLevel: eventual</c>
    /// header). Returns at most <paramref name="limit"/> rows — clamped at
    /// the callsite so a hostile payload can't ask for 10K users. Empty
    /// query returns the top-N displayName-ordered list so the popover
    /// has something to show on first focus.
    Task<IReadOnlyList<GraphUserStatus>> SearchUsersAsync(string? query, int limit, CancellationToken ct = default);
}

/// Minimal slice of a Graph <c>user</c> object — only what the login
/// callback and the user-admin UI consume. Kept tight to avoid leaking the
/// Graph SDK types into callers.
public sealed record GraphUserStatus(
    string Oid,
    bool AccountEnabled,
    string? UserPrincipalName,
    string? DisplayName,
    string? Mail);
