namespace Servicedesk.Infrastructure.Integrations.Telavox;

/// Telavox API surface used by Servicedesk. Two authentication families:
/// <list type="bullet">
/// <item><b>PAPI</b> — uses the single install-wide partner-token from
/// protected_secrets. Methods that take no explicit token are PAPI.</item>
/// <item><b>CAPI</b> — uses a per-agent token minted by
/// <see cref="CreateApiUserAsync"/>. The caller passes the token in
/// because the polling worker reads it from protected_secrets once and
/// reuses it across many ticks for the same agent.</item>
/// </list>
/// Every method translates non-2xx responses into
/// <see cref="TelavoxApiException"/> and writes one row to
/// <c>integration_audit</c> per attempt (latency + http_status +
/// truncated body snippet, never the token).
public interface ITelavoxApiClient
{
    /// GET /papi/v1/customers — lists every customer the configured
    /// partner-token has access to. Used by /test-connection to populate
    /// the customer-id dropdown on first setup.
    Task<IReadOnlyList<TelavoxCustomer>> ListCustomersAsync(CancellationToken ct);

    /// GET /papi/v1/customers/{customer}/extensions — full extensions
    /// list. Used by the integration page's per-agent dropdown when an
    /// admin links an SD user to a Telavox seat.
    Task<IReadOnlyList<TelavoxExtension>> ListExtensionsAsync(
        string customerId, CancellationToken ct);

    /// POST /papi/v1/customers/{customer}/api-users — provisions a fresh
    /// CAPI token tied to the supplied descriptive email + free-text
    /// description. The email becomes Telavox's primary-key for this
    /// api-user — the same string must be supplied to
    /// <see cref="DeleteApiUserAsync"/> on revoke.
    Task<TelavoxCreateApiUserResult> CreateApiUserAsync(
        string customerId,
        string email,
        string description,
        CancellationToken ct);

    /// DELETE /papi/v1/customers/{customer}/api-users/{email} — revokes
    /// the CAPI token. A 404 is treated as success (already gone) so a
    /// repeated revoke or a manual cleanup in Telavox doesn't surface as
    /// an admin-facing error.
    Task DeleteApiUserAsync(
        string customerId,
        string email,
        CancellationToken ct);

    /// GET /origo/api/v1/me/calls?state=active — returns the agent's
    /// current call, if any. CAPI is per-user: the supplied token alone
    /// identifies which agent we're polling, no customer-id needed in
    /// the URL. Returns null when no active call.
    Task<TelavoxCall?> GetCurrentCallAsync(
        string capiToken, CancellationToken ct);
}
