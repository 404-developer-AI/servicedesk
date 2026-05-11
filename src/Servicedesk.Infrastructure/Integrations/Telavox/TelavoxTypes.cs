namespace Servicedesk.Infrastructure.Integrations.Telavox;

/// Telavox customer (PAPI tenant). One PAPI partner-token typically scopes
/// access to multiple customers; the admin pins exactly one via the
/// integration page's Test connection dropdown.
public sealed record TelavoxCustomer(string Id, string Name);

/// Telavox extension (a phone-line/seat inside a customer). Shown in the
/// per-agent dropdown on the integration page. UserEmail (when present)
/// gives the admin a hint about which extension belongs to which Telavox
/// user without forcing a second PAPI call.
public sealed record TelavoxExtension(
    string Id,
    string Number,
    string? Name,
    string? UserEmail);

/// Result of the two-step PAPI provisioning flow:
/// <list type="number">
/// <item>POST <c>/v1/customers/{customer}/api-users?name=&lt;name&gt;</c> — creates
/// the api-user shell, response carries the api-user <c>key</c>.</item>
/// <item>POST <c>/v1/customers/{customer}/api-users/{key}/tokens</c> — mints a
/// CAPI bearer-token, response carries <c>bearerToken</c>.</item>
/// </list>
/// <see cref="Name"/> is the human-readable label the PAPI was called with
/// (Telavox does not RFC-validate it; we keep it for traceability and to
/// surface in the agent-mapping UI). <see cref="UserId"/> is the
/// Telavox-side api-user key (e.g. <c>apiUser-123</c>); it goes into
/// <see cref="TelavoxAgentLink.TelavoxUserId"/> and is the path-param the
/// DELETE flow targets on revoke. <see cref="Token"/> is the bearer the
/// provisioning service immediately writes to <c>protected_secrets</c> and
/// forgets — nothing else in the codebase holds the plaintext after that.
public sealed record TelavoxCreateApiUserResult(
    string Name,
    string UserId,
    string Token);

/// Telavox call snapshot, as returned by CAPI for one agent. State is the
/// raw upstream value (Telavox uses uppercase strings like RINGING,
/// ANSWERED, ENDED); the worker compares against
/// <see cref="Servicedesk.Infrastructure.Settings.SettingKeys.Telavox.PopupTriggerMode"/>
/// to decide whether to fire a popup. The number fields are intentionally
/// nullable: an outbound call has no FromNumber from Telavox's perspective,
/// an inbound from a hidden-CLI caller has none either.
public sealed record TelavoxCall(
    string CallId,
    string State,
    string? FromNumber,
    string? ToNumber,
    DateTimeOffset? StartUtc);

/// One row of <c>telavox_agent_links</c>. <see cref="TelavoxUserId"/> is
/// the PAPI api-user key (e.g. <c>apiUser-123</c>) returned by the create
/// flow; this is the path-segment the DELETE endpoint takes on revoke.
/// <see cref="CapiUserName"/> is the human-readable label the api-user was
/// minted with — useful for cross-checking with the Telavox admin UI and
/// for audit. The DB column is still named <c>capi_user_email</c> for
/// backwards compat with v0.0.34 commit B, but post-D the value is a name,
/// not an email (PAPI uses <c>?name=...</c> not an email body field).
public sealed record TelavoxAgentLink(
    Guid Id,
    Guid UserId,
    string TelavoxExtension,
    string TelavoxUserId,
    string CapiUserName,
    DateTime ProvisionedUtc,
    DateTime? LastPollUtc,
    string? LastPollError,
    int ConsecutiveErrors);

/// Resolved Telavox connection-state for the integration page tile.
/// Mirrors the Adsolut state-resolver shape so the SPA can render both
/// integrations through the same component.
public enum TelavoxConnectionState
{
    /// Master kill-switch is off (Telavox.Enabled=false). Tile shows
    /// "Disabled".
    Disabled = 0,

    /// Partner-token is missing. Tile shows "Not connected".
    NotConfigured = 1,

    /// Partner-token is set but no customer-id has been pinned. Tile
    /// shows "Test connection to pick a customer".
    NoCustomerSelected = 2,

    /// Partner-token set, customer-id pinned, but no agents linked yet.
    /// Tile shows "Ready — link agents to receive calls".
    Ready = 3,

    /// Fully configured + at least one linked agent. Tile shows "Active".
    Active = 4,
}
