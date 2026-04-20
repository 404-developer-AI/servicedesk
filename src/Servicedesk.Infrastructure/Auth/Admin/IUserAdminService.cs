using Servicedesk.Infrastructure.Auth.Microsoft;

namespace Servicedesk.Infrastructure.Auth.Admin;

/// Admin-side CRUD + M365 provisioning on the <c>users</c> table. All
/// mutating calls take the current admin's id so the service can enforce
/// the self-lockout guards (you cannot demote / deactivate / delete your
/// own row) and the last-admin guard (there must always be at least one
/// active Admin). Read calls are caller-agnostic — list access is already
/// gated at the endpoint level by <c>RequireAdmin</c>.
public interface IUserAdminService
{
    Task<IReadOnlyList<UserAdminRow>> ListAllAsync(CancellationToken ct = default);

    Task<UserAdminRow?> GetByIdAsync(Guid userId, CancellationToken ct = default);

    /// Adds a local-password user. Role must be Agent or Admin (Customer
    /// accounts live in the customer-portal flow, not here). Password is
    /// validated against <c>Security.Password.MinimumLength</c> and hashed
    /// with the project's Argon2id hasher before insert. Email is
    /// lower-cased + trimmed before the collision check.
    Task<AddLocalUserResult> AddLocalUserAsync(
        string email,
        string password,
        string role,
        Guid actingAdminId,
        CancellationToken ct = default);

    /// Adds a Microsoft user. Server re-reads the OID from Graph to
    /// verify it exists + get the authoritative email (never trusting the
    /// client-supplied email). Rejects if the email or OID is already in
    /// use, or if the OID points at a disabled account.
    Task<AddMicrosoftUserResult> AddMicrosoftUserAsync(
        string oid,
        string role,
        Guid actingAdminId,
        CancellationToken ct = default);

    /// Converts a Local user's row to Microsoft in one transaction:
    /// sets auth_mode/external_provider/external_subject, clears
    /// password_hash, deletes the TOTP secret + recovery-code rows.
    /// Guards: acting admin cannot upgrade their own row (they'd lose
    /// access until the next sign-in); target must currently be Local;
    /// target OID must not already be linked on another row.
    Task<UpgradeToMicrosoftResult> UpgradeToMicrosoftAsync(
        Guid userId,
        string oid,
        Guid actingAdminId,
        CancellationToken ct = default);

    /// Changes a user's role (Agent ↔ Admin). Customer is not a valid
    /// target here — customer accounts live in the customer portal flow
    /// (v0.1.x) and are managed separately. Guards: acting admin cannot
    /// change their own role; cannot demote the last active Admin.
    Task<UpdateRoleResult> UpdateRoleAsync(
        Guid userId,
        string role,
        Guid actingAdminId,
        CancellationToken ct = default);

    /// Activates or deactivates. Deactivating revokes all open sessions
    /// so the target is logged out across every browser / tab. Guards:
    /// acting admin cannot deactivate their own row; cannot deactivate
    /// the last active Admin.
    Task<SetActiveResult> SetActiveAsync(
        Guid userId,
        bool active,
        Guid actingAdminId,
        CancellationToken ct = default);

    /// Permanently deletes a row. FK cascades wipe sessions + TOTP.
    /// Guards: acting admin cannot delete their own row; cannot delete
    /// the last active Admin. Cannot delete users that have authored
    /// ticket content (see <see cref="DeleteResult.BlockedReferences"/>
    /// — those users must be deactivated instead).
    Task<DeleteResult> DeleteAsync(
        Guid userId,
        Guid actingAdminId,
        CancellationToken ct = default);
}

/// Projection for the /settings/users table. Fields are whitelisted so a
/// future column on <c>users</c> doesn't leak into the admin UI without
/// an explicit add here.
public sealed record UserAdminRow(
    Guid Id,
    string Email,
    string Role,
    string AuthMode,
    string? ExternalSubject,
    bool IsActive,
    bool TwoFactorEnabled,
    DateTime CreatedUtc,
    DateTime? LastLoginUtc);

// ---- Result types ------------------------------------------------------

public abstract record AddMicrosoftUserResult
{
    public sealed record Created(UserAdminRow Row) : AddMicrosoftUserResult;
    public sealed record InvalidRole : AddMicrosoftUserResult;
    public sealed record OidNotFound : AddMicrosoftUserResult;
    public sealed record OidAlreadyLinked : AddMicrosoftUserResult;
    public sealed record EmailAlreadyUsed : AddMicrosoftUserResult;
    public sealed record AzureAccountDisabled : AddMicrosoftUserResult;
    public sealed record MissingGraphConfig : AddMicrosoftUserResult;
}

public abstract record AddLocalUserResult
{
    public sealed record Created(UserAdminRow Row) : AddLocalUserResult;
    public sealed record InvalidEmail : AddLocalUserResult;
    public sealed record InvalidRole : AddLocalUserResult;
    public sealed record WeakPassword(int MinimumLength) : AddLocalUserResult;
    public sealed record EmailAlreadyUsed : AddLocalUserResult;
}

public abstract record UpgradeToMicrosoftResult
{
    public sealed record Upgraded(UserAdminRow Row) : UpgradeToMicrosoftResult;
    public sealed record UserNotFound : UpgradeToMicrosoftResult;
    public sealed record NotLocalUser : UpgradeToMicrosoftResult;
    public sealed record OidNotFound : UpgradeToMicrosoftResult;
    public sealed record OidAlreadyLinked : UpgradeToMicrosoftResult;
    public sealed record AzureAccountDisabled : UpgradeToMicrosoftResult;
    public sealed record SelfUpgradeForbidden : UpgradeToMicrosoftResult;
    public sealed record MissingGraphConfig : UpgradeToMicrosoftResult;
}

public abstract record UpdateRoleResult
{
    public sealed record Updated(UserAdminRow Row) : UpdateRoleResult;
    public sealed record UserNotFound : UpdateRoleResult;
    public sealed record InvalidRole : UpdateRoleResult;
    public sealed record SelfChangeForbidden : UpdateRoleResult;
    public sealed record LastAdminForbidden : UpdateRoleResult;
}

public abstract record SetActiveResult
{
    public sealed record Updated(UserAdminRow Row) : SetActiveResult;
    public sealed record UserNotFound : SetActiveResult;
    public sealed record SelfChangeForbidden : SetActiveResult;
    public sealed record LastAdminForbidden : SetActiveResult;
}

public abstract record DeleteResult
{
    public sealed record Deleted : DeleteResult;
    public sealed record UserNotFound : DeleteResult;
    public sealed record SelfDeleteForbidden : DeleteResult;
    public sealed record LastAdminForbidden : DeleteResult;
    public sealed record BlockedReferences(IReadOnlyList<string> Reasons) : DeleteResult;
}
