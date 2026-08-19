using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Logging;
using Servicedesk.Domain.Tickets;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Auth;
using Servicedesk.Infrastructure.Auth.Sessions;
using Servicedesk.Infrastructure.Auth.Totp;
using Servicedesk.Infrastructure.Mail.Ingest;
using Servicedesk.Infrastructure.Persistence.Companies;
using Servicedesk.Infrastructure.Persistence.Taxonomy;
using Servicedesk.Infrastructure.Persistence.Tickets;
using Servicedesk.Infrastructure.Realtime;
using Servicedesk.Infrastructure.Settings;
using Servicedesk.Infrastructure.Sla;

namespace Servicedesk.Infrastructure.Portal;

/// Who performs an admin-side portal action (for audit + approver columns).
public sealed record PortalActor(Guid UserId, string Email, string Role, string? Ip, string? UserAgent);

/// Anonymous caller context (audit + registration metadata).
public sealed record PortalCaller(string? Ip, string? UserAgent, string? Host);

public sealed record RegisterCommand(string Email, string Password, string DisplayName, string? TurnstileToken);

public enum RegisterOutcome
{
    /// The generic success answer — also returned when the email is already
    /// known (no enumeration). A verification mail was sent when applicable.
    Accepted,
    PortalDisabled,
    RegistrationDisabled,
    InvalidEmail,
    InvalidName,
    WeakPassword,
    TurnstileFailed,
    /// Turnstile on but misconfigured (no secret) — admin-visible, user sees a generic refusal.
    Misconfigured,
    MailFailed,
}

public sealed record RegisterResult(RegisterOutcome Outcome, string? Detail = null);

public enum TokenOutcome { Ok, Invalid, Expired, AlreadyUsed }

public sealed record VerifyEmailResult(TokenOutcome Outcome, Guid? UserId = null, bool AlreadyVerified = false);

public enum ApproveOutcome { Approved, NotPending, NotFound, ContactAlreadyLinked, InvalidRole, InvalidCompany }

public sealed record ApproveResult(ApproveOutcome Outcome, Guid? ContactId = null);

public enum InviteOutcome { Sent, InvalidEmail, InvalidRole, EmailTaken, ContactNotFound, ContactHasAccount, MailFailed, PortalDisabled }

public sealed record InviteResult(InviteOutcome Outcome, Guid? InvitationId = null);

public sealed record InvitationInfo(TokenOutcome Outcome, string? Email = null, string? DisplayName = null, string? CompanyName = null);

public enum AcceptInviteOutcome { Created, Invalid, Expired, EmailTaken, WeakPassword, ContactHasAccount }

public sealed record AcceptInviteResult(AcceptInviteOutcome Outcome, Guid? UserId = null, string? Email = null);

public enum ResetPasswordOutcome { Done, Invalid, Expired, WeakPassword }

public interface IPortalAccountService
{
    Task<bool> IsPortalEnabledAsync(CancellationToken ct);
    Task<RegisterResult> RegisterAsync(RegisterCommand cmd, PortalCaller caller, CancellationToken ct);
    Task<VerifyEmailResult> VerifyEmailAsync(string rawToken, PortalCaller caller, CancellationToken ct);
    Task<ApproveResult> ApproveAsync(Guid userId, Guid? companyId, string companyRole, PortalActor actor, CancellationToken ct);
    Task<bool> RejectAsync(Guid userId, string? reason, PortalActor actor, CancellationToken ct);
    Task<InviteResult> InviteAsync(string email, string displayName, Guid? contactId, Guid? companyId, string companyRole, PortalActor actor, CancellationToken ct);
    Task<bool> ResendInvitationAsync(Guid invitationId, PortalActor actor, CancellationToken ct);
    Task<bool> RevokeInvitationAsync(Guid invitationId, PortalActor actor, CancellationToken ct);
    Task<InvitationInfo> DescribeInvitationAsync(string rawToken, CancellationToken ct);
    Task<AcceptInviteResult> AcceptInvitationAsync(string rawToken, string password, PortalCaller caller, CancellationToken ct);
    Task ForgotPasswordAsync(string email, PortalCaller caller, CancellationToken ct);
    Task<ResetPasswordOutcome> ResetPasswordAsync(string rawToken, string newPassword, PortalCaller caller, CancellationToken ct);
    Task<bool> SetActiveAsync(Guid userId, bool active, PortalActor actor, CancellationToken ct);
    Task<bool> DeleteAsync(Guid userId, PortalActor actor, CancellationToken ct);
    Task<bool> ResetTotpAsync(Guid userId, PortalActor actor, CancellationToken ct);
    Task<bool> RevokeSessionsAsync(Guid userId, PortalActor actor, CancellationToken ct);
    Task<bool> ResendVerificationAsync(Guid userId, PortalActor actor, CancellationToken ct);
    /// Company suggested for a registrant from company_domains (email domain match).
    Task<(Guid Id, string Name)?> SuggestCompanyAsync(string email, CancellationToken ct);
    /// Validates a password against the portal policy; returns null when OK,
    /// else a short reason code.
    Task<string?> ValidatePasswordAsync(string password, CancellationToken ct);
}

public sealed class PortalAccountService : IPortalAccountService
{
    private readonly IPortalAccountRepository _accounts;
    private readonly IPortalTokenService _tokens;
    private readonly IPortalMailService _mail;
    private readonly ITurnstileVerifier _turnstile;
    private readonly IUserService _users;
    private readonly IPasswordHasher _hasher;
    private readonly ISessionService _sessions;
    private readonly ITotpService _totp;
    private readonly ISettingsService _settings;
    private readonly IAuditLogger _audit;
    private readonly ICompanyRepository _companies;
    private readonly IContactLookupService _contactLookup;
    private readonly ITicketRepository _tickets;
    private readonly ITaxonomyRepository _taxonomy;
    private readonly ISlaEngine _sla;
    private readonly ITicketListNotifier _notifier;
    private readonly ILogger<PortalAccountService> _logger;

    public PortalAccountService(
        IPortalAccountRepository accounts,
        IPortalTokenService tokens,
        IPortalMailService mail,
        ITurnstileVerifier turnstile,
        IUserService users,
        IPasswordHasher hasher,
        ISessionService sessions,
        ITotpService totp,
        ISettingsService settings,
        IAuditLogger audit,
        ICompanyRepository companies,
        IContactLookupService contactLookup,
        ITicketRepository tickets,
        ITaxonomyRepository taxonomy,
        ISlaEngine sla,
        ITicketListNotifier notifier,
        ILogger<PortalAccountService> logger)
    {
        _accounts = accounts;
        _tokens = tokens;
        _mail = mail;
        _turnstile = turnstile;
        _users = users;
        _hasher = hasher;
        _sessions = sessions;
        _totp = totp;
        _settings = settings;
        _audit = audit;
        _companies = companies;
        _contactLookup = contactLookup;
        _tickets = tickets;
        _taxonomy = taxonomy;
        _sla = sla;
        _notifier = notifier;
        _logger = logger;
    }

    public Task<bool> IsPortalEnabledAsync(CancellationToken ct) =>
        _settings.GetAsync<bool>(SettingKeys.Portal.Enabled, ct);

    // ---- registration -----------------------------------------------------

    public async Task<RegisterResult> RegisterAsync(RegisterCommand cmd, PortalCaller caller, CancellationToken ct)
    {
        if (!await IsPortalEnabledAsync(ct)) return new(RegisterOutcome.PortalDisabled);
        if (!await _settings.GetAsync<bool>(SettingKeys.Portal.RegistrationEnabled, ct))
            return new(RegisterOutcome.RegistrationDisabled);

        var email = NormalizeEmail(cmd.Email);
        if (email is null) return new(RegisterOutcome.InvalidEmail);
        var name = NormalizeName(cmd.DisplayName);
        if (name is null) return new(RegisterOutcome.InvalidName);
        var pwdProblem = await ValidatePasswordAsync(cmd.Password, ct);
        if (pwdProblem is not null) return new(RegisterOutcome.WeakPassword, pwdProblem);

        // Anti-bot gate runs BEFORE any row or mail is produced. Fail closed.
        if (await _settings.GetAsync<bool>(SettingKeys.Portal.TurnstileEnabled, ct))
        {
            var action = await _settings.GetAsync<string>(SettingKeys.Portal.TurnstileAction, ct);
            var verdict = await _turnstile.VerifyAsync(
                cmd.TurnstileToken, caller.Ip, action ?? string.Empty, await ExpectedHostnameAsync(caller, ct), ct);
            if (!verdict.Success)
            {
                await _audit.LogAsync(new AuditEvent(
                    PortalEventTypes.RegisterRefusedTurnstile, "customer", "anon", Target: email,
                    ClientIp: caller.Ip, UserAgent: caller.UserAgent,
                    Payload: new { reason = verdict.Reason }), ct);
                return verdict.Reason == "secret_missing"
                    ? new(RegisterOutcome.Misconfigured, verdict.Reason)
                    : new(RegisterOutcome.TurnstileFailed, verdict.Reason);
            }
        }

        // Existing email (any role, any state) → same generic answer. The
        // only side effect: a still-unverified registration gets its
        // verification mail re-sent (cooldown-guarded) so a lost mail is
        // recoverable without an agent.
        var existing = await _users.FindByEmailAsync(email, ct);
        if (existing is not null)
        {
            var account = await _accounts.GetByUserIdAsync(existing.Id, ct);
            if (account is not null && account.Status == PortalAccountStatus.PendingVerification)
                await TrySendVerificationAsync(account, ct);
            return new(RegisterOutcome.Accepted);
        }

        var hash = _hasher.Hash(cmd.Password);
        var userId = await _accounts.CreatePendingRegistrationAsync(email, hash, name, caller.Ip, caller.UserAgent, ct);
        if (userId is null)
        {
            // Lost the race against a concurrent registration — generic answer.
            return new(RegisterOutcome.Accepted);
        }

        await _audit.LogAsync(new AuditEvent(
            PortalEventTypes.RegisterRequested, email, "Customer", Target: userId.Value.ToString(),
            ClientIp: caller.Ip, UserAgent: caller.UserAgent, Payload: new { displayName = name }), ct);

        var created = await _accounts.GetByUserIdAsync(userId.Value, ct);
        var sent = created is not null && await TrySendVerificationAsync(created, ct, ignoreCooldown: true);
        return sent ? new(RegisterOutcome.Accepted) : new(RegisterOutcome.MailFailed);
    }

    private async Task<bool> TrySendVerificationAsync(PortalAccountRow account, CancellationToken ct, bool ignoreCooldown = false)
    {
        if (!ignoreCooldown && await InCooldownAsync(account.Email, PortalTokenKind.EmailVerification, ct))
            return true; // silently skip; the earlier mail is still valid

        var hours = Math.Max(1, await _settings.GetAsync<int>(SettingKeys.Portal.VerificationTokenHours, ct));
        var validity = TimeSpan.FromHours(hours);
        var (raw, hash) = _tokens.Mint();
        await _accounts.CreateTokenAsync(
            PortalTokenKind.EmailVerification, hash, account.Email, account.UserId, null, null, null,
            account.DisplayName, null, DateTime.UtcNow.Add(validity), ct);
        var link = await BuildLinkAsync("/portal/verify-email", raw, ct);
        return await _mail.SendAsync(PortalMailKind.EmailVerification, account.Email, account.DisplayName, link, validity, ct);
    }

    public async Task<VerifyEmailResult> VerifyEmailAsync(string rawToken, PortalCaller caller, CancellationToken ct)
    {
        var token = await LoadTokenAsync(rawToken, PortalTokenKind.EmailVerification, ct);
        if (token.Outcome != TokenOutcome.Ok || token.Row?.UserId is null)
            return new(token.Outcome);

        var userId = token.Row.UserId.Value;
        if (!await _accounts.ConsumeTokenAsync(token.Row.Id, ct))
            return new(TokenOutcome.AlreadyUsed);

        var flipped = await _accounts.MarkEmailVerifiedAsync(userId, ct);
        var account = await _accounts.GetByUserIdAsync(userId, ct);
        if (!flipped)
        {
            // Token was live but the account already moved on (verified
            // earlier via a different link, rejected, …). Report politely.
            return new(TokenOutcome.Ok, userId, AlreadyVerified: true);
        }

        await _audit.LogAsync(new AuditEvent(
            PortalEventTypes.EmailVerified, account?.Email ?? userId.ToString(), "Customer",
            Target: userId.ToString(), ClientIp: caller.Ip, UserAgent: caller.UserAgent), ct);

        if (account is not null)
            await TryCreateRegistrationTicketAsync(account, ct);

        return new(TokenOutcome.Ok, userId);
    }

    /// The Tickets.NewUserCreatesNotificationTicket hook: one system ticket
    /// per verified registration in Portal.RegistrationQueueId so the team
    /// sees it in their normal flow and approves from the ticket.
    private async Task TryCreateRegistrationTicketAsync(PortalAccountRow account, CancellationToken ct)
    {
        try
        {
            if (!await _settings.GetAsync<bool>(SettingKeys.Tickets.NewUserCreatesNotificationTicket, ct)) return;
            var queueRaw = await _settings.GetAsync<string>(SettingKeys.Portal.RegistrationQueueId, ct);
            if (!Guid.TryParse(queueRaw, out var queueId)) return;
            if (await _taxonomy.GetQueueAsync(queueId, ct) is null)
            {
                _logger.LogWarning("Portal.RegistrationQueueId {QueueId} does not exist — registration ticket skipped.", queueId);
                return;
            }
            var defaults = await ResolveDefaultsAsync(ct);
            if (defaults is null) return;

            var contact = await _contactLookup.EnsureByEmailAsync(account.Email, account.DisplayName, ct);
            var suggestion = await SuggestCompanyAsync(account.Email, ct);
            var resolution = await _contactLookup.ResolveCompanyForNewTicketAsync(contact.Id, ct);

            var subject = $"Portal registration: {account.DisplayName} <{account.Email}>";
            var bodyText =
                $"A customer registered on the portal and confirmed their email address.\n\n" +
                $"Name: {account.DisplayName}\nEmail: {account.Email}\n" +
                (suggestion is { } s ? $"Suggested company (by email domain): {s.Name}\n" : "Suggested company: none (unknown email domain)\n") +
                $"Registered from: {account.RegistrationIp ?? "unknown"}\n\n" +
                "Approve or reject the account from the portal-registration card on this ticket, or from Settings → Portal.";
            var bodyHtml =
                "<p>A customer registered on the portal and confirmed their email address.</p>" +
                $"<p><strong>Name:</strong> {WebUtility.HtmlEncode(account.DisplayName)}<br/>" +
                $"<strong>Email:</strong> {WebUtility.HtmlEncode(account.Email)}<br/>" +
                (suggestion is { } s2
                    ? $"<strong>Suggested company</strong> (by email domain): {WebUtility.HtmlEncode(s2.Name)}<br/>"
                    : "<strong>Suggested company:</strong> none (unknown email domain)<br/>") +
                $"<strong>Registered from:</strong> {WebUtility.HtmlEncode(account.RegistrationIp ?? "unknown")}</p>" +
                "<p>Approve or reject the account from the portal-registration card on this ticket, or from Settings → Portal.</p>";

            var ticket = await _tickets.CreateAsync(new NewTicket(
                Subject: subject,
                BodyText: bodyText,
                BodyHtml: bodyHtml,
                RequesterContactId: contact.Id,
                QueueId: queueId,
                StatusId: defaults.Value.StatusId,
                PriorityId: defaults.Value.PriorityId,
                CategoryId: null,
                AssigneeUserId: null,
                Source: TicketSource.System.ToString(),
                CompanyId: resolution.CompanyId,
                AwaitingCompanyAssignment: resolution.Awaiting,
                CompanyResolvedVia: resolution.ResolvedVia), ct);

            await _accounts.SetApprovalTicketAsync(account.UserId, ticket.Id, ct);
            await _sla.OnTicketCreatedAsync(ticket.Id, ct);
            await _notifier.NotifyUpdatedAsync(ticket.Id, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The registration itself succeeded; approvals remain possible
            // from Settings → Portal. Never fail the customer-facing step.
            _logger.LogError(ex, "Failed to create the registration ticket for portal account {UserId}.", account.UserId);
        }
    }

    private async Task<(Guid StatusId, Guid PriorityId)?> ResolveDefaultsAsync(CancellationToken ct)
    {
        var statuses = await _taxonomy.ListStatusesAsync(ct);
        var priorities = await _taxonomy.ListPrioritiesAsync(ct);
        var status = statuses.FirstOrDefault(s => s.IsDefault && s.IsActive) ?? statuses.FirstOrDefault(s => s.IsActive);
        var priority = priorities.FirstOrDefault(p => p.IsDefault && p.IsActive) ?? priorities.FirstOrDefault(p => p.IsActive);
        if (status is null || priority is null) return null;
        return (status.Id, priority.Id);
    }

    public async Task<(Guid Id, string Name)?> SuggestCompanyAsync(string email, CancellationToken ct)
    {
        var at = email.LastIndexOf('@');
        if (at < 0 || at == email.Length - 1) return null;
        var domain = email[(at + 1)..].Trim().ToLowerInvariant();
        var company = await _companies.FindCompanyByDomainAsync(domain, ct);
        return company is null ? null : (company.Id, company.Name);
    }

    // ---- approval ---------------------------------------------------------

    public async Task<ApproveResult> ApproveAsync(Guid userId, Guid? companyId, string companyRole, PortalActor actor, CancellationToken ct)
    {
        if (!IsValidCompanyRole(companyRole)) return new(ApproveOutcome.InvalidRole);
        var account = await _accounts.GetByUserIdAsync(userId, ct);
        if (account is null) return new(ApproveOutcome.NotFound);
        if (account.Status != PortalAccountStatus.PendingApproval) return new(ApproveOutcome.NotPending);
        if (companyId.HasValue && await _companies.GetCompanyAsync(companyId.Value, ct) is null)
            return new(ApproveOutcome.InvalidCompany);

        var contact = await _contactLookup.EnsureByEmailAsync(account.Email, account.DisplayName, ct);
        var other = await _accounts.GetByContactIdAsync(contact.Id, ct);
        if (other is not null && other.UserId != userId) return new(ApproveOutcome.ContactAlreadyLinked);

        if (companyId.HasValue && contact.PrimaryCompanyId != companyId)
            await _companies.SetPrimaryCompanyAsync(contact.Id, companyId, ct);
        await _accounts.SetContactCompanyRoleAsync(contact.Id, companyRole, ct);

        if (!await _accounts.ApproveAsync(userId, contact.Id, actor.UserId, ct))
            return new(ApproveOutcome.NotPending);

        var companyName = companyId.HasValue ? (await _companies.GetCompanyAsync(companyId.Value, ct))?.Name : null;
        await _audit.LogAsync(new AuditEvent(
            PortalEventTypes.Approved, actor.Email, actor.Role, Target: userId.ToString(),
            ClientIp: actor.Ip, UserAgent: actor.UserAgent,
            Payload: new { email = account.Email, contactId = contact.Id, companyId, companyRole }), ct);

        await TryPostSystemNoteAsync(account.ApprovalTicketId,
            $"Portal registration approved by {actor.Email}" +
            (companyName is not null ? $" — company: {companyName}" : " — no company") +
            $", role: {companyRole}.", ct);

        var loginLink = await BuildLinkAsync("/portal/login", null, ct);
        await _mail.SendAsync(PortalMailKind.Approved, account.Email, account.DisplayName, loginLink, null, ct);
        return new(ApproveOutcome.Approved, contact.Id);
    }

    public async Task<bool> RejectAsync(Guid userId, string? reason, PortalActor actor, CancellationToken ct)
    {
        var account = await _accounts.GetByUserIdAsync(userId, ct);
        if (account is null) return false;
        if (!await _accounts.RejectAsync(userId, actor.UserId, reason, ct)) return false;
        await _accounts.RevokeTokensAsync(account.Email, PortalTokenKind.EmailVerification, ct);
        await _sessions.RevokeAllForUserAsync(userId, ct);
        await _audit.LogAsync(new AuditEvent(
            PortalEventTypes.Rejected, actor.Email, actor.Role, Target: userId.ToString(),
            ClientIp: actor.Ip, UserAgent: actor.UserAgent,
            Payload: new { email = account.Email, reason }), ct);
        await TryPostSystemNoteAsync(account.ApprovalTicketId,
            $"Portal registration rejected by {actor.Email}" +
            (string.IsNullOrWhiteSpace(reason) ? "." : $": {reason.Trim()}"), ct);
        return true;
    }

    private async Task TryPostSystemNoteAsync(Guid? ticketId, string text, CancellationToken ct)
    {
        if (ticketId is null) return;
        try
        {
            await _tickets.AddEventAsync(ticketId.Value, new NewTicketEvent(
                EventType: TicketEventType.SystemNote.ToString(),
                BodyText: text,
                BodyHtml: null,
                IsInternal: true,
                AuthorUserId: null), ct);
            await _notifier.NotifyUpdatedAsync(ticketId.Value, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not post the portal system note on ticket {TicketId}.", ticketId);
        }
    }

    // ---- invitations ------------------------------------------------------

    public async Task<InviteResult> InviteAsync(
        string email, string displayName, Guid? contactId, Guid? companyId, string companyRole, PortalActor actor, CancellationToken ct)
    {
        if (!await IsPortalEnabledAsync(ct)) return new(InviteOutcome.PortalDisabled);
        if (!IsValidCompanyRole(companyRole)) return new(InviteOutcome.InvalidRole);

        string? resolvedEmail;
        var name = NormalizeName(displayName) ?? string.Empty;
        if (contactId.HasValue)
        {
            var contact = await _companies.GetContactAsync(contactId.Value, ct);
            if (contact is null) return new(InviteOutcome.ContactNotFound);
            if (await _accounts.GetByContactIdAsync(contact.Id, ct) is not null) return new(InviteOutcome.ContactHasAccount);
            // The account must sign in with the contact's address — the
            // contact is the identity, the invite email is never overridden.
            resolvedEmail = NormalizeEmail(contact.Email);
            if (resolvedEmail is null) return new(InviteOutcome.InvalidEmail);
            if (name.Length == 0) name = $"{contact.FirstName} {contact.LastName}".Trim();
            companyId ??= contact.PrimaryCompanyId;
        }
        else
        {
            resolvedEmail = NormalizeEmail(email);
            if (resolvedEmail is null) return new(InviteOutcome.InvalidEmail);
            var existingContact = await _companies.GetContactByEmailAsync(resolvedEmail, ct);
            if (existingContact is not null)
            {
                if (await _accounts.GetByContactIdAsync(existingContact.Id, ct) is not null) return new(InviteOutcome.ContactHasAccount);
                contactId = existingContact.Id;
                if (name.Length == 0) name = $"{existingContact.FirstName} {existingContact.LastName}".Trim();
                companyId ??= existingContact.PrimaryCompanyId;
            }
        }
        if (name.Length == 0) name = resolvedEmail;
        if (await _users.FindByEmailAsync(resolvedEmail, ct) is not null) return new(InviteOutcome.EmailTaken);

        var hours = Math.Max(1, await _settings.GetAsync<int>(SettingKeys.Portal.InvitationTokenHours, ct));
        var validity = TimeSpan.FromHours(hours);
        var (raw, hash) = _tokens.Mint();
        var id = await _accounts.CreateTokenAsync(
            PortalTokenKind.Invitation, hash, resolvedEmail, null, contactId, companyId, companyRole,
            name, actor.UserId, DateTime.UtcNow.Add(validity), ct);
        var link = await BuildLinkAsync("/portal/invitation", raw, ct);
        if (!await _mail.SendAsync(PortalMailKind.Invitation, resolvedEmail, name, link, validity, ct))
        {
            await _accounts.RevokeTokenAsync(id, ct);
            return new(InviteOutcome.MailFailed);
        }
        await _audit.LogAsync(new AuditEvent(
            PortalEventTypes.Invited, actor.Email, actor.Role, Target: resolvedEmail,
            ClientIp: actor.Ip, UserAgent: actor.UserAgent,
            Payload: new { invitationId = id, contactId, companyId, companyRole }), ct);
        return new(InviteOutcome.Sent, id);
    }

    public async Task<bool> ResendInvitationAsync(Guid invitationId, PortalActor actor, CancellationToken ct)
    {
        var row = await _accounts.GetTokenByIdAsync(invitationId, ct);
        if (row is null || row.Kind != PortalTokenKind.Invitation || row.UsedUtc is not null) return false;
        var hours = Math.Max(1, await _settings.GetAsync<int>(SettingKeys.Portal.InvitationTokenHours, ct));
        var validity = TimeSpan.FromHours(hours);
        var (raw, hash) = _tokens.Mint();
        var id = await _accounts.CreateTokenAsync(
            PortalTokenKind.Invitation, hash, row.Email, null, row.ContactId, row.CompanyId, row.CompanyRole,
            row.DisplayName, actor.UserId, DateTime.UtcNow.Add(validity), ct);
        var link = await BuildLinkAsync("/portal/invitation", raw, ct);
        if (!await _mail.SendAsync(PortalMailKind.Invitation, row.Email, row.DisplayName, link, validity, ct))
        {
            await _accounts.RevokeTokenAsync(id, ct);
            return false;
        }
        await _audit.LogAsync(new AuditEvent(
            PortalEventTypes.Invited, actor.Email, actor.Role, Target: row.Email,
            ClientIp: actor.Ip, UserAgent: actor.UserAgent,
            Payload: new { invitationId = id, resendOf = invitationId }), ct);
        return true;
    }

    public async Task<bool> RevokeInvitationAsync(Guid invitationId, PortalActor actor, CancellationToken ct)
    {
        var row = await _accounts.GetTokenByIdAsync(invitationId, ct);
        if (row is null || row.Kind != PortalTokenKind.Invitation) return false;
        if (!await _accounts.RevokeTokenAsync(invitationId, ct)) return false;
        await _audit.LogAsync(new AuditEvent(
            "portal.invitation.revoked", actor.Email, actor.Role, Target: row.Email,
            ClientIp: actor.Ip, UserAgent: actor.UserAgent, Payload: new { invitationId }), ct);
        return true;
    }

    public async Task<InvitationInfo> DescribeInvitationAsync(string rawToken, CancellationToken ct)
    {
        var token = await LoadTokenAsync(rawToken, PortalTokenKind.Invitation, ct);
        if (token.Outcome != TokenOutcome.Ok || token.Row is null) return new(token.Outcome);
        string? companyName = null;
        if (token.Row.CompanyId.HasValue)
            companyName = (await _companies.GetCompanyAsync(token.Row.CompanyId.Value, ct))?.Name;
        return new(TokenOutcome.Ok, token.Row.Email, token.Row.DisplayName, companyName);
    }

    public async Task<AcceptInviteResult> AcceptInvitationAsync(string rawToken, string password, PortalCaller caller, CancellationToken ct)
    {
        var token = await LoadTokenAsync(rawToken, PortalTokenKind.Invitation, ct);
        if (token.Outcome == TokenOutcome.Expired) return new(AcceptInviteOutcome.Expired);
        if (token.Outcome != TokenOutcome.Ok || token.Row is null) return new(AcceptInviteOutcome.Invalid);
        var row = token.Row;

        if (await ValidatePasswordAsync(password, ct) is not null) return new(AcceptInviteOutcome.WeakPassword);
        if (await _users.FindByEmailAsync(row.Email, ct) is not null) return new(AcceptInviteOutcome.EmailTaken);

        var contact = row.ContactId.HasValue
            ? await _companies.GetContactAsync(row.ContactId.Value, ct)
            : null;
        contact ??= await _contactLookup.EnsureByEmailAsync(row.Email, row.DisplayName, ct);
        if (await _accounts.GetByContactIdAsync(contact.Id, ct) is not null) return new(AcceptInviteOutcome.ContactHasAccount);

        // Single use first: two browsers racing on the same link cannot both
        // create an account (the second sees AlreadyUsed → Invalid).
        if (!await _accounts.ConsumeTokenAsync(row.Id, ct)) return new(AcceptInviteOutcome.Invalid);

        var hash = _hasher.Hash(password);
        var userId = await _accounts.CreateInvitedAccountAsync(row.Email, hash, row.DisplayName, contact.Id, row.CreatedByUserId, ct);
        if (userId is null) return new(AcceptInviteOutcome.EmailTaken);

        if (row.CompanyId.HasValue && contact.PrimaryCompanyId != row.CompanyId)
            await _companies.SetPrimaryCompanyAsync(contact.Id, row.CompanyId, ct);
        if (IsValidCompanyRole(row.CompanyRole ?? string.Empty))
            await _accounts.SetContactCompanyRoleAsync(contact.Id, row.CompanyRole!, ct);

        await _audit.LogAsync(new AuditEvent(
            PortalEventTypes.InvitationAccepted, row.Email, "Customer", Target: userId.Value.ToString(),
            ClientIp: caller.Ip, UserAgent: caller.UserAgent,
            Payload: new { invitationId = row.Id, contactId = contact.Id, companyId = row.CompanyId, companyRole = row.CompanyRole }), ct);
        return new(AcceptInviteOutcome.Created, userId, row.Email);
    }

    // ---- password reset ---------------------------------------------------

    public async Task ForgotPasswordAsync(string email, PortalCaller caller, CancellationToken ct)
    {
        var normalized = NormalizeEmail(email);
        if (normalized is null) return;
        var user = await _users.FindByEmailAsync(normalized, ct);
        if (user is null || user.RoleName != "Customer" || user.AuthMode != AuthModes.Local) return;
        var account = await _accounts.GetByUserIdAsync(user.Id, ct);
        if (account is null || account.Status != PortalAccountStatus.Active) return;
        if (await InCooldownAsync(normalized, PortalTokenKind.PasswordReset, ct)) return;

        var minutes = Math.Max(5, await _settings.GetAsync<int>(SettingKeys.Portal.PasswordResetTokenMinutes, ct));
        var validity = TimeSpan.FromMinutes(minutes);
        var (raw, hash) = _tokens.Mint();
        await _accounts.CreateTokenAsync(
            PortalTokenKind.PasswordReset, hash, normalized, user.Id, null, null, null,
            account.DisplayName, null, DateTime.UtcNow.Add(validity), ct);
        var link = await BuildLinkAsync("/portal/reset-password", raw, ct);
        await _mail.SendAsync(PortalMailKind.PasswordReset, normalized, account.DisplayName, link, validity, ct);
        await _audit.LogAsync(new AuditEvent(
            PortalEventTypes.PasswordResetRequested, normalized, "Customer", Target: user.Id.ToString(),
            ClientIp: caller.Ip, UserAgent: caller.UserAgent), ct);
    }

    public async Task<ResetPasswordOutcome> ResetPasswordAsync(string rawToken, string newPassword, PortalCaller caller, CancellationToken ct)
    {
        var token = await LoadTokenAsync(rawToken, PortalTokenKind.PasswordReset, ct);
        if (token.Outcome == TokenOutcome.Expired) return ResetPasswordOutcome.Expired;
        if (token.Outcome != TokenOutcome.Ok || token.Row?.UserId is null) return ResetPasswordOutcome.Invalid;
        if (await ValidatePasswordAsync(newPassword, ct) is not null) return ResetPasswordOutcome.WeakPassword;

        var userId = token.Row.UserId.Value;
        var user = await _users.FindByIdAsync(userId, ct);
        if (user is null || user.RoleName != "Customer") return ResetPasswordOutcome.Invalid;
        var account = await _accounts.GetByUserIdAsync(userId, ct);
        if (account is null || account.Status != PortalAccountStatus.Active) return ResetPasswordOutcome.Invalid;

        if (!await _accounts.ConsumeTokenAsync(token.Row.Id, ct)) return ResetPasswordOutcome.Invalid;
        await _users.UpdatePasswordHashAsync(userId, _hasher.Hash(newPassword), ct);
        // A reset ends every open session — the usual "I lost my laptop" case.
        await _sessions.RevokeAllForUserAsync(userId, ct);
        await _audit.LogAsync(new AuditEvent(
            PortalEventTypes.PasswordResetCompleted, user.Email, "Customer", Target: userId.ToString(),
            ClientIp: caller.Ip, UserAgent: caller.UserAgent), ct);
        return ResetPasswordOutcome.Done;
    }

    // ---- admin account actions -------------------------------------------

    public async Task<bool> SetActiveAsync(Guid userId, bool active, PortalActor actor, CancellationToken ct)
    {
        var account = await _accounts.GetByUserIdAsync(userId, ct);
        if (account is null) return false;
        if (!await _accounts.SetActiveAsync(userId, active, ct)) return false;
        if (!active) await _sessions.RevokeAllForUserAsync(userId, ct);
        await _audit.LogAsync(new AuditEvent(
            active ? PortalEventTypes.Reactivated : PortalEventTypes.Deactivated, actor.Email, actor.Role,
            Target: userId.ToString(), ClientIp: actor.Ip, UserAgent: actor.UserAgent,
            Payload: new { email = account.Email }), ct);
        return true;
    }

    public async Task<bool> DeleteAsync(Guid userId, PortalActor actor, CancellationToken ct)
    {
        var account = await _accounts.GetByUserIdAsync(userId, ct);
        if (account is null) return false;
        await _sessions.RevokeAllForUserAsync(userId, ct);
        if (!await _accounts.DeleteAsync(userId, ct)) return false;
        await _audit.LogAsync(new AuditEvent(
            PortalEventTypes.Deleted, actor.Email, actor.Role, Target: userId.ToString(),
            ClientIp: actor.Ip, UserAgent: actor.UserAgent,
            Payload: new { email = account.Email, status = account.Status }), ct);
        return true;
    }

    public async Task<bool> ResetTotpAsync(Guid userId, PortalActor actor, CancellationToken ct)
    {
        var account = await _accounts.GetByUserIdAsync(userId, ct);
        if (account is null) return false;
        await _totp.DisableAsync(userId, ct);
        await _sessions.RevokeAllForUserAsync(userId, ct);
        await _audit.LogAsync(new AuditEvent(
            PortalEventTypes.TotpReset, actor.Email, actor.Role, Target: userId.ToString(),
            ClientIp: actor.Ip, UserAgent: actor.UserAgent, Payload: new { email = account.Email }), ct);
        return true;
    }

    public async Task<bool> RevokeSessionsAsync(Guid userId, PortalActor actor, CancellationToken ct)
    {
        var account = await _accounts.GetByUserIdAsync(userId, ct);
        if (account is null) return false;
        await _sessions.RevokeAllForUserAsync(userId, ct);
        await _audit.LogAsync(new AuditEvent(
            PortalEventTypes.SessionsRevoked, actor.Email, actor.Role, Target: userId.ToString(),
            ClientIp: actor.Ip, UserAgent: actor.UserAgent, Payload: new { email = account.Email }), ct);
        return true;
    }

    public async Task<bool> ResendVerificationAsync(Guid userId, PortalActor actor, CancellationToken ct)
    {
        var account = await _accounts.GetByUserIdAsync(userId, ct);
        if (account is null || account.Status != PortalAccountStatus.PendingVerification) return false;
        return await TrySendVerificationAsync(account, ct, ignoreCooldown: true);
    }

    // ---- helpers ----------------------------------------------------------

    public async Task<string?> ValidatePasswordAsync(string password, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(password)) return "password_required";
        var min = Math.Max(8, await _settings.GetAsync<int>(SettingKeys.Portal.PasswordMinimumLength, ct));
        if (password.Length < min) return $"password_too_short:{min}";
        if (password.Length > 256) return "password_too_long";
        return null;
    }

    private sealed record LoadedToken(TokenOutcome Outcome, PortalTokenRow? Row);

    private async Task<LoadedToken> LoadTokenAsync(string rawToken, string expectedKind, CancellationToken ct)
    {
        var hash = _tokens.HashForLookup(rawToken);
        if (hash is null) return new(TokenOutcome.Invalid, null);
        var row = await _accounts.GetTokenByHashAsync(hash, ct);
        if (row is null || row.Kind != expectedKind) return new(TokenOutcome.Invalid, null);
        if (row.UsedUtc is not null) return new(TokenOutcome.AlreadyUsed, row);
        if (row.RevokedUtc is not null) return new(TokenOutcome.Invalid, row);
        if (row.ExpiresUtc <= DateTime.UtcNow) return new(TokenOutcome.Expired, row);
        return new(TokenOutcome.Ok, row);
    }

    private async Task<bool> InCooldownAsync(string email, string kind, CancellationToken ct)
    {
        var minutes = await _settings.GetAsync<int>(SettingKeys.Portal.MailResendCooldownMinutes, ct);
        if (minutes <= 0) return false;
        var last = await _accounts.GetLatestTokenCreatedUtcAsync(email, kind, ct);
        return last is not null && last.Value.AddMinutes(minutes) > DateTime.UtcNow;
    }

    private async Task<string> BuildLinkAsync(string path, string? rawToken, CancellationToken ct)
    {
        var baseUrl = await _settings.GetAsync<string>(SettingKeys.App.PublicBaseUrl, ct) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(baseUrl))
            _logger.LogWarning("App.PublicBaseUrl is empty; portal mail links will be relative and may not work.");
        var prefix = string.IsNullOrWhiteSpace(baseUrl) ? string.Empty : baseUrl.TrimEnd('/');
        return rawToken is null
            ? $"{prefix}{path}"
            : $"{prefix}{path}?token={Uri.EscapeDataString(rawToken)}";
    }

    private async Task<string?> ExpectedHostnameAsync(PortalCaller caller, CancellationToken ct)
    {
        var baseUrl = await _settings.GetAsync<string>(SettingKeys.App.PublicBaseUrl, ct);
        if (!string.IsNullOrWhiteSpace(baseUrl) && Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri))
            return uri.Host;
        // No public URL configured: fall back to the request host (minus port).
        if (string.IsNullOrWhiteSpace(caller.Host)) return null;
        var host = caller.Host.Trim();
        var colon = host.LastIndexOf(':');
        if (colon > 0 && !host.Contains(']')) host = host[..colon];
        return host;
    }

    internal static string? NormalizeEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return null;
        var trimmed = email.Trim();
        if (trimmed.Length > 254 || trimmed.Contains(' ') || trimmed.Count(c => c == '@') != 1) return null;
        try
        {
            var parsed = new MailAddress(trimmed);
            if (!string.Equals(parsed.Address, trimmed, StringComparison.OrdinalIgnoreCase)) return null;
            var at = trimmed.LastIndexOf('@');
            if (!trimmed[(at + 1)..].Contains('.')) return null;
            return trimmed.ToLowerInvariant();
        }
        catch (FormatException)
        {
            return null;
        }
    }

    internal static string? NormalizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var collapsed = string.Join(' ', name.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (collapsed.Length < 2 || collapsed.Length > 120) return null;
        if (collapsed.Any(c => char.IsControl(c) || c == '<' || c == '>')) return null;
        return collapsed;
    }

    internal static bool IsValidCompanyRole(string role) =>
        role is "Member" or "TicketManager";
}
