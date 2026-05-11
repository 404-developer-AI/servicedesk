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

/// Result of POST /papi/v1/customers/{customer}/api-users. Carries the
/// CAPI token verbatim — the provisioning service immediately writes it to
/// protected_secrets and forgets it; nothing else in the codebase holds the
/// plaintext after that. UserId is the Telavox-side opaque identifier we
/// store on telavox_agent_links so a future re-link can target the same
/// underlying Telavox user.
public sealed record TelavoxCreateApiUserResult(
    string Email,
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

/// One row of <c>telavox_agent_links</c>. The CapiUserEmail is the
/// /papi/api-users primary-key Telavox uses to identify this token-bearer;
/// it's stored alongside the secret-key so revocation can target the
/// correct DELETE endpoint without a second PAPI call.
public sealed record TelavoxAgentLink(
    Guid Id,
    Guid UserId,
    string TelavoxExtension,
    string TelavoxUserId,
    string CapiUserEmail,
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
