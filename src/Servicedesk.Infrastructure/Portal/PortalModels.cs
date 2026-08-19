namespace Servicedesk.Infrastructure.Portal;

/// Lifecycle states of a portal account (mirrors chk_portal_accounts_status).
public static class PortalAccountStatus
{
    public const string PendingVerification = "PendingVerification";
    public const string PendingApproval = "PendingApproval";
    public const string Active = "Active";
    public const string Rejected = "Rejected";
    public const string Deactivated = "Deactivated";
}

public static class PortalAccountOrigin
{
    public const string Registration = "Registration";
    public const string Invitation = "Invitation";
}

/// Kinds of one-time mail links (mirrors chk_portal_tokens_kind).
public static class PortalTokenKind
{
    public const string EmailVerification = "EmailVerification";
    public const string Invitation = "Invitation";
    public const string PasswordReset = "PasswordReset";
}

/// Audit event names for the portal surface (dotted style, see AuthEventTypes).
public static class PortalEventTypes
{
    public const string RegisterRequested = "portal.register.requested";
    public const string RegisterRefusedTurnstile = "portal.register.refused_turnstile";
    public const string EmailVerified = "portal.email.verified";
    public const string Approved = "portal.account.approved";
    public const string Rejected = "portal.account.rejected";
    public const string Invited = "portal.account.invited";
    public const string InvitationAccepted = "portal.invitation.accepted";
    public const string Deactivated = "portal.account.deactivated";
    public const string Reactivated = "portal.account.reactivated";
    public const string Deleted = "portal.account.deleted";
    public const string TotpReset = "portal.account.totp_reset";
    public const string SessionsRevoked = "portal.account.sessions_revoked";
    public const string LoginSuccess = "portal.login.success";
    public const string LoginFailed = "portal.login.failed";
    public const string LoginLockedOut = "portal.login.locked_out";
    public const string LoginRefusedState = "portal.login.refused_state";
    public const string Logout = "portal.logout";
    public const string TwoFactorEnrolled = "portal.2fa.enrolled";
    public const string TwoFactorChallengeSuccess = "portal.2fa.challenge_success";
    public const string TwoFactorChallengeFailed = "portal.2fa.challenge_failed";
    public const string PasswordResetRequested = "portal.password.reset_requested";
    public const string PasswordResetCompleted = "portal.password.reset_completed";
    public const string TicketCreated = "portal.ticket.created";
    public const string TicketReplied = "portal.ticket.replied";
    public const string AttachmentUploaded = "portal.ticket.attachment_uploaded";
    public const string AttachmentViewed = "portal.ticket.attachment_view";
    public const string TurnstileSecretUpdated = "portal.turnstile.secret_updated";
    public const string TurnstileSecretDeleted = "portal.turnstile.secret_deleted";
    /// Agent-side login endpoint refused a Customer account (they must use the portal).
    public const string AgentLoginRejectedCustomer = "login_rejected_customer";
}

/// One portal account joined with its users row + linked contact/company.
public sealed record PortalAccountRow(
    Guid UserId,
    string Email,
    string Status,
    string DisplayName,
    string Origin,
    bool IsActive,
    Guid? ContactId,
    string? ContactFirstName,
    string? ContactLastName,
    string? ContactCompanyRole,
    Guid? CompanyId,
    string? CompanyName,
    string? RegistrationIp,
    DateTime? EmailVerifiedUtc,
    Guid? ApprovalTicketId,
    long? ApprovalTicketNumber,
    Guid? ApprovedByUserId,
    string? ApprovedByEmail,
    DateTime? ApprovedUtc,
    Guid? RejectedByUserId,
    DateTime? RejectedUtc,
    string? RejectionReason,
    Guid? InvitedByUserId,
    string? InvitedByEmail,
    bool TwoFactorEnrolled,
    DateTime? LastLoginUtc,
    DateTime CreatedUtc,
    DateTime UpdatedUtc);

/// One company a customer may act in, with the portal role for that link
/// (Member = own tickets at that company, TicketManager = every ticket of
/// that company). Supplier links never appear here.
public sealed record PortalCompanyAccess(Guid CompanyId, string CompanyName, string Role, bool IsPrimary)
{
    public bool IsTicketManager => string.Equals(Role, "TicketManager", StringComparison.Ordinal);
}

/// A requested (company, portal role) pair on approve / invite.
public sealed record PortalCompanyLinkRequest(Guid CompanyId, string Role);

/// Minimal authenticated-viewer projection resolved per request: which
/// contact and companies the customer session maps onto. Null contact
/// means the account is not linked yet (must never happen for Active, but
/// the query layer treats it as "sees nothing").
public sealed record PortalViewer(
    Guid UserId,
    string Email,
    string DisplayName,
    string Status,
    Guid? ContactId,
    string? ContactFirstName,
    string? ContactLastName,
    IReadOnlyList<PortalCompanyAccess> Companies)
{
    public PortalCompanyAccess? Company(Guid companyId) => Companies.FirstOrDefault(c => c.CompanyId == companyId);
    public Guid[] AllCompanyIds => Companies.Select(c => c.CompanyId).ToArray();
    public Guid[] ManagerCompanyIds => Companies.Where(c => c.IsTicketManager).Select(c => c.CompanyId).ToArray();
    /// Default company for the list/new-ticket when the client sent none:
    /// the primary link, else the first.
    public PortalCompanyAccess? DefaultCompany => Companies.FirstOrDefault(c => c.IsPrimary) ?? Companies.FirstOrDefault();
}

/// Row used by the viewer lookup before it is folded into PortalViewer.
public sealed record PortalViewerBase(
    Guid UserId,
    string Email,
    string DisplayName,
    string Status,
    Guid? ContactId,
    string? ContactFirstName,
    string? ContactLastName);

public sealed record PortalTokenRow(
    Guid Id,
    string Kind,
    string Email,
    Guid? UserId,
    Guid? ContactId,
    Guid? CompanyId,
    string? CompanyRole,
    string DisplayName,
    Guid? CreatedByUserId,
    DateTime CreatedUtc,
    DateTime ExpiresUtc,
    DateTime? UsedUtc,
    DateTime? RevokedUtc,
    string? CompanyLinksJson = null)
{
    public bool IsUsable(DateTime nowUtc) => UsedUtc is null && RevokedUtc is null && ExpiresUtc > nowUtc;
}

/// Pending invitation surfaced on the contact page / admin list.
public sealed record PortalInvitationRow(
    Guid Id,
    string Email,
    Guid? ContactId,
    Guid? CompanyId,
    string? CompanyName,
    string? CompanyRole,
    string DisplayName,
    Guid? CreatedByUserId,
    string? CreatedByEmail,
    DateTime CreatedUtc,
    DateTime ExpiresUtc,
    DateTime? UsedUtc,
    DateTime? RevokedUtc,
    string? CompanyLinksJson = null);
