namespace Servicedesk.Infrastructure.Integrations.Telavox;

/// Telavox API surface used by Servicedesk. Two authentication families:
/// <list type="bullet">
/// <item><b>PAPI</b> — uses the single install-wide partner-token from
/// protected_secrets. Methods that take no explicit token are PAPI.</item>
/// <item><b>CAPI</b> — uses a per-agent bearer-token minted by
/// <see cref="CreateApiUserAsync"/>. The caller passes the token + the
/// agent's extension in because the polling worker reads them from
/// protected_secrets / the link row once and reuses them across many
/// ticks for the same agent.</item>
/// </list>
/// Every method translates non-2xx responses into
/// <see cref="TelavoxApiException"/> and writes one row to
/// <c>integration_audit</c> per attempt (latency + http_status +
/// truncated body snippet, never the token).
public interface ITelavoxApiClient
{
    /// GET /partner2/api/papi/v1/customers — lists every customer the
    /// configured partner-token has access to. Used by /test-connection
    /// to populate the customer-id dropdown on first setup.
    Task<IReadOnlyList<TelavoxCustomer>> ListCustomersAsync(CancellationToken ct);

    /// GET /partner2/api/papi/v1/customers/{customer}/extensions — full
    /// extensions list. Used by the integration page's per-agent dropdown
    /// when an admin links an SD user to a Telavox seat.
    Task<IReadOnlyList<TelavoxExtension>> ListExtensionsAsync(
        string customerId, CancellationToken ct);

    /// Provisions a fresh CAPI bearer-token bound to a new PAPI api-user.
    /// Two-step PAPI flow per the swagger:
    /// <list type="number">
    /// <item>POST <c>/v1/customers/{customer}/api-users?name={name}</c> —
    /// creates the api-user shell, response carries the api-user
    /// <c>key</c>.</item>
    /// <item>POST <c>/v1/customers/{customer}/api-users/{key}/tokens</c> —
    /// mints the bearer-token.</item>
    /// </list>
    /// Returns the api-user key (so DELETE can target it) and the bearer
    /// verbatim. If step 2 fails after step 1 succeeded, the partial
    /// upstream api-user is best-effort rolled back inside this method —
    /// callers never see a half-provisioned state.
    Task<TelavoxCreateApiUserResult> CreateApiUserAsync(
        string customerId,
        string name,
        CancellationToken ct);

    /// DELETE /partner2/api/papi/v1/customers/{customer}/api-users/{apiUserKey} —
    /// revokes the CAPI api-user (and all its bearer-tokens by extension).
    /// A 404 is treated as success (already gone) so a repeated revoke or
    /// a manual cleanup in Telavox doesn't surface as an admin-facing
    /// error.
    Task DeleteApiUserAsync(
        string customerId,
        string apiUserKey,
        CancellationToken ct);

    /// GET /api/capi/v1/extensions/{extension}/calls — returns the
    /// ongoing-calls list for the supplied extension, using the per-agent
    /// CAPI bearer-token. Returns the first ongoing call, or null when
    /// none. The worker uses this to detect a ring/answer transition for
    /// the linked agent.
    Task<TelavoxCall?> GetCurrentCallAsync(
        string extension,
        string capiToken,
        CancellationToken ct);
}
