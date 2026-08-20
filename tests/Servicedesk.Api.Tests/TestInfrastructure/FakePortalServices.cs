using Servicedesk.Infrastructure.Portal;

namespace Servicedesk.Api.Tests.TestInfrastructure;

/// v0.1.0 — no-op portal repository/service doubles so the portal
/// endpoints (whose handlers inject Npgsql-backed singletons) resolve in the
/// DB-less baseline host. Authorization/CSRF/404-while-disabled tests only
/// need the pipeline to reach the handler; the data layer stays empty.
public sealed class FakePortalAccountRepository : IPortalAccountRepository
{
    public Task<Guid?> CreatePendingRegistrationAsync(string email, string passwordHash, string displayName, string? ip, string? userAgent, CancellationToken ct) => Task.FromResult<Guid?>(null);
    public Task<Guid?> CreateInvitedAccountAsync(string email, string passwordHash, string displayName, Guid contactId, Guid? invitedByUserId, CancellationToken ct) => Task.FromResult<Guid?>(null);
    /// Test seam (v0.1.1) — the impersonate endpoint needs an Active row.
    public PortalAccountRow? Account { get; set; }
    public Task<PortalAccountRow?> GetByUserIdAsync(Guid userId, CancellationToken ct) =>
        Task.FromResult(Account is { } a && a.UserId == userId ? Account : null);
    public Task<PortalAccountRow?> GetByEmailAsync(string email, CancellationToken ct) => Task.FromResult<PortalAccountRow?>(null);
    public Task<PortalAccountRow?> GetByContactIdAsync(Guid contactId, CancellationToken ct) => Task.FromResult<PortalAccountRow?>(null);
    public Task<PortalAccountRow?> GetByApprovalTicketAsync(Guid ticketId, CancellationToken ct) => Task.FromResult<PortalAccountRow?>(null);
    public Task<IReadOnlyList<PortalAccountRow>> ListAsync(IReadOnlyList<string>? statuses, string? search, int limit, CancellationToken ct) => Task.FromResult<IReadOnlyList<PortalAccountRow>>(Array.Empty<PortalAccountRow>());
    public Task<int> CountByStatusAsync(string status, CancellationToken ct) => Task.FromResult(0);
    public Task<bool> MarkEmailVerifiedAsync(Guid userId, CancellationToken ct) => Task.FromResult(false);
    public Task SetApprovalTicketAsync(Guid userId, Guid ticketId, CancellationToken ct) => Task.CompletedTask;
    public Task<bool> ApproveAsync(Guid userId, Guid contactId, Guid approvedByUserId, CancellationToken ct) => Task.FromResult(false);
    public Task<bool> RejectAsync(Guid userId, Guid rejectedByUserId, string? reason, CancellationToken ct) => Task.FromResult(false);
    public Task<bool> SetActiveAsync(Guid userId, bool active, CancellationToken ct) => Task.FromResult(false);
    public Task<bool> DeleteAsync(Guid userId, CancellationToken ct) => Task.FromResult(false);
    public Task<PortalViewer?> GetViewerAsync(Guid userId, CancellationToken ct) => Task.FromResult<PortalViewer?>(null);
    public Task SetContactCompanyRoleAsync(Guid contactId, string companyRole, CancellationToken ct) => Task.CompletedTask;
    public Task<Guid> CreateTokenAsync(string kind, byte[] tokenHash, string email, Guid? userId, Guid? contactId, Guid? companyId, string? companyRole, string displayName, Guid? createdByUserId, DateTime expiresUtc, CancellationToken ct, string? companyLinksJson = null) => Task.FromResult(Guid.NewGuid());
    public Task<bool> SetPortalRoleAsync(Guid contactId, Guid companyId, string portalRole, CancellationToken ct) => Task.FromResult(false);
    public Task<PortalTokenRow?> GetTokenByHashAsync(byte[] tokenHash, CancellationToken ct) => Task.FromResult<PortalTokenRow?>(null);
    public Task<PortalTokenRow?> GetTokenByIdAsync(Guid id, CancellationToken ct) => Task.FromResult<PortalTokenRow?>(null);
    public Task<bool> ConsumeTokenAsync(Guid id, CancellationToken ct) => Task.FromResult(false);
    public Task<int> RevokeTokensAsync(string email, string kind, CancellationToken ct) => Task.FromResult(0);
    public Task<bool> RevokeTokenAsync(Guid id, CancellationToken ct) => Task.FromResult(false);
    public Task<DateTime?> GetLatestTokenCreatedUtcAsync(string email, string kind, CancellationToken ct) => Task.FromResult<DateTime?>(null);
    public Task<IReadOnlyList<PortalInvitationRow>> ListInvitationsAsync(Guid? contactId, bool includeExpired, CancellationToken ct) => Task.FromResult<IReadOnlyList<PortalInvitationRow>>(Array.Empty<PortalInvitationRow>());
}

public sealed class FakePortalTicketRepository : IPortalTicketRepository
{
    public Task<PortalTicketPage> ListAsync(PortalViewer viewer, Guid? companyId, PortalTicketFilter filter, string? search, int page, int pageSize, CancellationToken ct) =>
        Task.FromResult(new PortalTicketPage(Array.Empty<PortalTicketListItem>(), 0, page, pageSize));
    public Task<PortalTicketHeader?> GetHeaderAsync(PortalViewer viewer, Guid ticketId, CancellationToken ct) => Task.FromResult<PortalTicketHeader?>(null);
    public Task<bool> EventIsCustomerVisibleAsync(Guid ticketId, long eventId, CancellationToken ct) => Task.FromResult(false);
    public Task<bool> MailMessageIsCustomerVisibleAsync(Guid ticketId, Guid mailMessageId, CancellationToken ct) => Task.FromResult(false);
    public Task<bool> PortalMessageBelongsToContactAsync(Guid ticketId, long eventId, Guid contactId, CancellationToken ct) => Task.FromResult(false);
}

public sealed class FakePortalAccountService : IPortalAccountService
{
    private static Exception NotWired() => new InvalidOperationException("Portal account service is not wired in the baseline test host.");
    public Task<bool> IsPortalEnabledAsync(CancellationToken ct) => Task.FromResult(false);
    public Task<RegisterResult> RegisterAsync(RegisterCommand cmd, PortalCaller caller, CancellationToken ct) => Task.FromResult(new RegisterResult(RegisterOutcome.PortalDisabled));
    public Task<VerifyEmailResult> VerifyEmailAsync(string rawToken, PortalCaller caller, CancellationToken ct) => Task.FromResult(new VerifyEmailResult(TokenOutcome.Invalid));
    public Task<ApproveResult> ApproveAsync(Guid userId, IReadOnlyList<PortalCompanyLinkRequest> companies, PortalActor actor, CancellationToken ct) => throw NotWired();
    public Task<bool> SetPortalRoleAsync(Guid contactId, Guid companyId, string role, PortalActor actor, CancellationToken ct) => throw NotWired();
    public Task<bool> RejectAsync(Guid userId, string? reason, PortalActor actor, CancellationToken ct) => throw NotWired();
    public Task<InviteResult> InviteAsync(string email, string displayName, Guid? contactId, IReadOnlyList<PortalCompanyLinkRequest> companies, PortalActor actor, CancellationToken ct) => throw NotWired();
    public Task<bool> ResendInvitationAsync(Guid invitationId, PortalActor actor, CancellationToken ct) => throw NotWired();
    public Task<bool> RevokeInvitationAsync(Guid invitationId, PortalActor actor, CancellationToken ct) => throw NotWired();
    public Task<InvitationInfo> DescribeInvitationAsync(string rawToken, CancellationToken ct) => Task.FromResult(new InvitationInfo(TokenOutcome.Invalid));
    public Task<AcceptInviteResult> AcceptInvitationAsync(string rawToken, string password, PortalCaller caller, CancellationToken ct) => Task.FromResult(new AcceptInviteResult(AcceptInviteOutcome.Invalid));
    public Task ForgotPasswordAsync(string email, PortalCaller caller, CancellationToken ct) => Task.CompletedTask;
    public Task<ResetPasswordOutcome> ResetPasswordAsync(string rawToken, string newPassword, PortalCaller caller, CancellationToken ct) => Task.FromResult(ResetPasswordOutcome.Invalid);
    public Task<bool> SetActiveAsync(Guid userId, bool active, PortalActor actor, CancellationToken ct) => throw NotWired();
    public Task<bool> DeleteAsync(Guid userId, PortalActor actor, CancellationToken ct) => throw NotWired();
    public Task<bool> ResetTotpAsync(Guid userId, PortalActor actor, CancellationToken ct) => throw NotWired();
    public Task<bool> RevokeSessionsAsync(Guid userId, PortalActor actor, CancellationToken ct) => throw NotWired();
    public Task<bool> ResendVerificationAsync(Guid userId, PortalActor actor, CancellationToken ct) => throw NotWired();
    public Task<(Guid Id, string Name)?> SuggestCompanyAsync(string email, CancellationToken ct) => Task.FromResult<(Guid, string)?>(null);
    public Task<string?> ValidatePasswordAsync(string password, CancellationToken ct) => Task.FromResult<string?>(null);
}
