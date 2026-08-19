namespace Servicedesk.Infrastructure.Portal;

/// Dapper repository for portal accounts + one-time tokens. Every state
/// transition is a conditional UPDATE on the expected previous status so
/// two concurrent approvals/rejections cannot both "win".
public interface IPortalAccountRepository
{
    // ---- accounts ---------------------------------------------------------

    /// Creates the users row (role Customer, Local, is_active FALSE) and the
    /// PendingVerification portal_accounts row in one transaction. Returns
    /// null when the email is already taken (any role).
    Task<Guid?> CreatePendingRegistrationAsync(
        string email, string passwordHash, string displayName, string? ip, string? userAgent, CancellationToken ct);

    /// Creates an already-Active account from an accepted invitation, linked
    /// to <paramref name="contactId"/>. Returns null when the email is taken.
    Task<Guid?> CreateInvitedAccountAsync(
        string email, string passwordHash, string displayName, Guid contactId, Guid? invitedByUserId, CancellationToken ct);

    Task<PortalAccountRow?> GetByUserIdAsync(Guid userId, CancellationToken ct);
    Task<PortalAccountRow?> GetByEmailAsync(string email, CancellationToken ct);
    Task<PortalAccountRow?> GetByContactIdAsync(Guid contactId, CancellationToken ct);
    Task<PortalAccountRow?> GetByApprovalTicketAsync(Guid ticketId, CancellationToken ct);

    /// Lists accounts, newest first. <paramref name="statuses"/> null/empty = all.
    Task<IReadOnlyList<PortalAccountRow>> ListAsync(
        IReadOnlyList<string>? statuses, string? search, int limit, CancellationToken ct);

    Task<int> CountByStatusAsync(string status, CancellationToken ct);

    /// PendingVerification → PendingApproval. Returns false when the account
    /// was not in PendingVerification (already verified / rejected / …).
    Task<bool> MarkEmailVerifiedAsync(Guid userId, CancellationToken ct);

    Task SetApprovalTicketAsync(Guid userId, Guid ticketId, CancellationToken ct);

    /// PendingApproval → Active: links the contact, activates the users row.
    Task<bool> ApproveAsync(Guid userId, Guid contactId, Guid approvedByUserId, CancellationToken ct);

    /// PendingVerification|PendingApproval → Rejected.
    Task<bool> RejectAsync(Guid userId, Guid rejectedByUserId, string? reason, CancellationToken ct);

    /// Active → Deactivated (active=false) or Deactivated → Active (active=true).
    Task<bool> SetActiveAsync(Guid userId, bool active, CancellationToken ct);

    /// Hard-deletes the users row (cascades portal_accounts + tokens). Only
    /// rows with role Customer are ever deleted. Returns false when no such row.
    Task<bool> DeleteAsync(Guid userId, CancellationToken ct);

    /// Resolves the contact / company / role a customer session maps onto.
    Task<PortalViewer?> GetViewerAsync(Guid userId, CancellationToken ct);

    /// Sets contacts.company_role (Member | TicketManager) for a contact
    /// (legacy per-contact value, kept in step with the primary link).
    Task SetContactCompanyRoleAsync(Guid contactId, string companyRole, CancellationToken ct);

    /// Sets the portal role on one existing contact_companies link.
    /// Returns false when the link does not exist.
    Task<bool> SetPortalRoleAsync(Guid contactId, Guid companyId, string portalRole, CancellationToken ct);

    // ---- tokens -----------------------------------------------------------

    /// Stores a new token (revoking earlier unused tokens of the same kind
    /// for the same email so at most one link is live per purpose).
    Task<Guid> CreateTokenAsync(
        string kind, byte[] tokenHash, string email, Guid? userId, Guid? contactId,
        Guid? companyId, string? companyRole, string displayName, Guid? createdByUserId,
        DateTime expiresUtc, CancellationToken ct, string? companyLinksJson = null);

    Task<PortalTokenRow?> GetTokenByHashAsync(byte[] tokenHash, CancellationToken ct);
    Task<PortalTokenRow?> GetTokenByIdAsync(Guid id, CancellationToken ct);

    /// Marks the token used iff it is still live. False = already used /
    /// revoked / expired (race-safe single use).
    Task<bool> ConsumeTokenAsync(Guid id, CancellationToken ct);

    Task<int> RevokeTokensAsync(string email, string kind, CancellationToken ct);
    Task<bool> RevokeTokenAsync(Guid id, CancellationToken ct);

    /// When the most recent token of this kind for this email was created
    /// (used or not) — drives the resend cooldown.
    Task<DateTime?> GetLatestTokenCreatedUtcAsync(string email, string kind, CancellationToken ct);

    /// Live (unused, unrevoked, unexpired) invitations, newest first.
    /// <paramref name="contactId"/> filters to one contact; null = all.
    Task<IReadOnlyList<PortalInvitationRow>> ListInvitationsAsync(Guid? contactId, bool includeExpired, CancellationToken ct);
}
