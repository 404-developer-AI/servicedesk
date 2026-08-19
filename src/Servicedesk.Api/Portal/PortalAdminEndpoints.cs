using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Persistence.Taxonomy;
using Servicedesk.Infrastructure.Portal;
using Servicedesk.Infrastructure.Secrets;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Api.Portal;

/// v0.1.0 — agent/admin side of the customer portal: registration
/// approvals, invitations, account lifecycle, Turnstile secret, status.
/// Agents approve/reject/invite (they own the customer relationship);
/// destructive or configuration actions stay admin-only.
public static class PortalAdminEndpoints
{
    public static IEndpointRouteBuilder MapPortalAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var agent = app.MapGroup("/api/portal/admin")
            .WithTags("PortalAdmin")
            .RequireAuthorization(AuthorizationPolicies.RequireAgent);

        agent.MapGet("/accounts", ListAccounts).WithName("PortalAdminListAccounts").WithOpenApi();
        agent.MapGet("/accounts/{userId:guid}", GetAccount).WithName("PortalAdminGetAccount").WithOpenApi();
        agent.MapGet("/accounts/by-contact/{contactId:guid}", GetByContact).WithName("PortalAdminGetByContact").WithOpenApi();
        agent.MapGet("/accounts/by-ticket/{ticketId:guid}", GetByTicket).WithName("PortalAdminGetByTicket").WithOpenApi();
        agent.MapPost("/accounts/{userId:guid}/approve", Approve).WithName("PortalAdminApprove").WithOpenApi();
        agent.MapPost("/accounts/{userId:guid}/reject", Reject).WithName("PortalAdminReject").WithOpenApi();
        agent.MapPost("/accounts/{userId:guid}/deactivate", Deactivate).WithName("PortalAdminDeactivate").WithOpenApi();
        agent.MapPost("/accounts/{userId:guid}/reactivate", Reactivate).WithName("PortalAdminReactivate").WithOpenApi();
        agent.MapPost("/accounts/{userId:guid}/reset-totp", ResetTotp).WithName("PortalAdminResetTotp").WithOpenApi();
        agent.MapPost("/accounts/{userId:guid}/revoke-sessions", RevokeSessions).WithName("PortalAdminRevokeSessions").WithOpenApi();
        agent.MapPost("/accounts/{userId:guid}/resend-verification", ResendVerification).WithName("PortalAdminResendVerification").WithOpenApi();
        agent.MapGet("/invitations", ListInvitations).WithName("PortalAdminListInvitations").WithOpenApi();
        agent.MapPost("/invitations", Invite).WithName("PortalAdminInvite").WithOpenApi();
        agent.MapPut("/contacts/{contactId:guid}/companies/{companyId:guid}/role", SetPortalRole).WithName("PortalAdminSetPortalRole").WithOpenApi();
        agent.MapPost("/invitations/{id:guid}/resend", ResendInvitation).WithName("PortalAdminResendInvitation").WithOpenApi();
        agent.MapDelete("/invitations/{id:guid}", RevokeInvitation).WithName("PortalAdminRevokeInvitation").WithOpenApi();

        var admin = app.MapGroup("/api/portal/admin")
            .WithTags("PortalAdmin")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin);
        admin.MapGet("/status", Status).WithName("PortalAdminStatus").WithOpenApi();
        admin.MapDelete("/accounts/{userId:guid}", Delete).WithName("PortalAdminDeleteAccount").WithOpenApi();
        admin.MapGet("/turnstile/secret", GetTurnstileSecretStatus).WithName("PortalAdminTurnstileSecretStatus").WithOpenApi();
        admin.MapPut("/turnstile/secret", SetTurnstileSecret).WithName("PortalAdminSetTurnstileSecret").WithOpenApi();
        admin.MapDelete("/turnstile/secret", DeleteTurnstileSecret).WithName("PortalAdminDeleteTurnstileSecret").WithOpenApi();
        return app;
    }

    private static object Project(PortalAccountRow a) => new
    {
        userId = a.UserId,
        email = a.Email,
        status = a.Status,
        displayName = a.DisplayName,
        origin = a.Origin,
        isActive = a.IsActive,
        contactId = a.ContactId,
        contactName = a.ContactId is null ? null : $"{a.ContactFirstName} {a.ContactLastName}".Trim(),
        companyRole = a.ContactCompanyRole,
        companyId = a.CompanyId,
        companyName = a.CompanyName,
        registrationIp = a.RegistrationIp,
        emailVerifiedUtc = a.EmailVerifiedUtc,
        approvalTicketId = a.ApprovalTicketId,
        approvalTicketNumber = a.ApprovalTicketNumber,
        approvedByEmail = a.ApprovedByEmail,
        approvedUtc = a.ApprovedUtc,
        rejectedUtc = a.RejectedUtc,
        rejectionReason = a.RejectionReason,
        invitedByEmail = a.InvitedByEmail,
        twoFactorEnrolled = a.TwoFactorEnrolled,
        lastLoginUtc = a.LastLoginUtc,
        createdUtc = a.CreatedUtc,
        updatedUtc = a.UpdatedUtc,
    };

    private static object Project(PortalInvitationRow i) => new
    {
        id = i.Id,
        email = i.Email,
        contactId = i.ContactId,
        companyId = i.CompanyId,
        companyName = i.CompanyName,
        companyRole = i.CompanyRole,
        displayName = i.DisplayName,
        createdByEmail = i.CreatedByEmail,
        createdUtc = i.CreatedUtc,
        expiresUtc = i.ExpiresUtc,
        expired = i.ExpiresUtc <= DateTime.UtcNow,
    };

    // ---- status -----------------------------------------------------------

    private static async Task<IResult> Status(
        ISettingsService settings, IProtectedSecretStore secrets, IPortalMailService mail,
        IPortalAccountRepository accounts, ITaxonomyRepository taxonomy, CancellationToken ct)
    {
        var regQueueRaw = await settings.GetAsync<string>(SettingKeys.Portal.RegistrationQueueId, ct);
        var newQueueRaw = await settings.GetAsync<string>(SettingKeys.Portal.NewTicketQueueId, ct);
        var regQueueOk = Guid.TryParse(regQueueRaw, out var rq) && await taxonomy.GetQueueAsync(rq, ct) is not null;
        var newQueueOk = Guid.TryParse(newQueueRaw, out var nq) && await taxonomy.GetQueueAsync(nq, ct) is not null;
        var turnstileEnabled = await settings.GetAsync<bool>(SettingKeys.Portal.TurnstileEnabled, ct);
        var siteKey = await settings.GetAsync<string>(SettingKeys.Portal.TurnstileSiteKey, ct);
        var secretConfigured = await secrets.HasAsync(ProtectedSecretKeys.PortalTurnstileSecret, ct);
        var fromMailbox = await mail.ResolveFromMailboxAsync(ct);
        var publicBaseUrl = await settings.GetAsync<string>(SettingKeys.App.PublicBaseUrl, ct);
        return Results.Ok(new
        {
            enabled = await settings.GetAsync<bool>(SettingKeys.Portal.Enabled, ct),
            registrationEnabled = await settings.GetAsync<bool>(SettingKeys.Portal.RegistrationEnabled, ct),
            registrationTicketEnabled = await settings.GetAsync<bool>(SettingKeys.Tickets.NewUserCreatesNotificationTicket, ct),
            registrationQueueConfigured = regQueueOk,
            newTicketQueueConfigured = newQueueOk,
            fromMailbox,
            publicBaseUrlConfigured = !string.IsNullOrWhiteSpace(publicBaseUrl),
            turnstile = new
            {
                enabled = turnstileEnabled,
                siteKeyConfigured = !string.IsNullOrWhiteSpace(siteKey),
                secretConfigured,
                // Fail-closed warning: enabled without secret = registration refused.
                misconfigured = turnstileEnabled && (!secretConfigured || string.IsNullOrWhiteSpace(siteKey)),
            },
            counts = new
            {
                pendingVerification = await accounts.CountByStatusAsync(PortalAccountStatus.PendingVerification, ct),
                pendingApproval = await accounts.CountByStatusAsync(PortalAccountStatus.PendingApproval, ct),
                active = await accounts.CountByStatusAsync(PortalAccountStatus.Active, ct),
                deactivated = await accounts.CountByStatusAsync(PortalAccountStatus.Deactivated, ct),
                rejected = await accounts.CountByStatusAsync(PortalAccountStatus.Rejected, ct),
            },
        });
    }

    // ---- accounts ---------------------------------------------------------

    private static async Task<IResult> ListAccounts(
        [FromQuery] string? status, [FromQuery] string? search, [FromQuery] int? limit,
        IPortalAccountRepository accounts, CancellationToken ct)
    {
        var statuses = string.IsNullOrWhiteSpace(status)
            ? null
            : status.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(s => s is "PendingVerification" or "PendingApproval" or "Active" or "Rejected" or "Deactivated")
                .ToList();
        var rows = await accounts.ListAsync(statuses, search, limit ?? 200, ct);
        return Results.Ok(rows.Select(Project));
    }

    private static async Task<IResult> GetAccount(Guid userId, IPortalAccountRepository accounts, IPortalAccountService service, CancellationToken ct)
    {
        var row = await accounts.GetByUserIdAsync(userId, ct);
        if (row is null) return Results.NotFound();
        var suggestion = await service.SuggestCompanyAsync(row.Email, ct);
        return Results.Ok(new
        {
            account = Project(row),
            suggestedCompany = suggestion is { } s ? new { id = s.Id, name = s.Name } : null,
        });
    }

    private static async Task<IResult> GetByContact(Guid contactId, IPortalAccountRepository accounts, CancellationToken ct)
    {
        var row = await accounts.GetByContactIdAsync(contactId, ct);
        var invitations = await accounts.ListInvitationsAsync(contactId, includeExpired: true, ct);
        return Results.Ok(new
        {
            account = row is null ? null : Project(row),
            invitations = invitations.Select(Project),
        });
    }

    private static async Task<IResult> GetByTicket(Guid ticketId, IPortalAccountRepository accounts, IPortalAccountService service, CancellationToken ct)
    {
        var row = await accounts.GetByApprovalTicketAsync(ticketId, ct);
        if (row is null) return Results.Ok(new { account = (object?)null, suggestedCompany = (object?)null });
        var suggestion = await service.SuggestCompanyAsync(row.Email, ct);
        return Results.Ok(new
        {
            account = Project(row),
            suggestedCompany = suggestion is { } s ? new { id = s.Id, name = s.Name } : null,
        });
    }

    public sealed record CompanyLinkDto(Guid CompanyId, string? Role);

    /// Companies + roles; `companyId`/`companyRole` are the legacy single-pair
    /// shape (still accepted) and are folded in first.
    public sealed record ApproveRequest(Guid? CompanyId, string? CompanyRole, List<CompanyLinkDto>? Companies);

    private static IReadOnlyList<PortalCompanyLinkRequest> ToLinks(Guid? companyId, string? companyRole, List<CompanyLinkDto>? companies)
    {
        var list = new List<PortalCompanyLinkRequest>();
        if (companyId is { } single && single != Guid.Empty) list.Add(new PortalCompanyLinkRequest(single, companyRole ?? "Member"));
        foreach (var c in companies ?? new List<CompanyLinkDto>())
            if (c.CompanyId != Guid.Empty) list.Add(new PortalCompanyLinkRequest(c.CompanyId, c.Role ?? "Member"));
        return list;
    }

    private static async Task<IResult> Approve(Guid userId, [FromBody] ApproveRequest req, HttpContext http,
        IPortalAccountService service, IPortalAccountRepository accounts, CancellationToken ct)
    {
        var result = await service.ApproveAsync(userId, ToLinks(req.CompanyId, req.CompanyRole, req.Companies), PortalRequest.Actor(http), ct);
        return result.Outcome switch
        {
            ApproveOutcome.Approved => Results.Ok(new { account = Project((await accounts.GetByUserIdAsync(userId, ct))!) }),
            ApproveOutcome.NotFound => Results.NotFound(),
            ApproveOutcome.NotPending => Results.Conflict(new { error = "not_pending", message = "This registration is no longer awaiting approval." }),
            ApproveOutcome.ContactAlreadyLinked => Results.Conflict(new { error = "contact_linked", message = "The contact with this email address already has a portal account." }),
            ApproveOutcome.InvalidRole => Results.BadRequest(new { error = "invalid_role", message = "Role must be Member or TicketManager." }),
            ApproveOutcome.InvalidCompany => Results.BadRequest(new { error = "invalid_company", message = "Unknown company." }),
            _ => Results.StatusCode(StatusCodes.Status500InternalServerError),
        };
    }

    public sealed record RejectRequest(string? Reason);

    private static async Task<IResult> Reject(Guid userId, [FromBody] RejectRequest? req, HttpContext http,
        IPortalAccountService service, IPortalAccountRepository accounts, CancellationToken ct)
    {
        var ok = await service.RejectAsync(userId, req?.Reason, PortalRequest.Actor(http), ct);
        if (!ok)
            return await accounts.GetByUserIdAsync(userId, ct) is null
                ? Results.NotFound()
                : Results.Conflict(new { error = "not_pending", message = "This registration is no longer pending." });
        return Results.Ok(new { account = Project((await accounts.GetByUserIdAsync(userId, ct))!) });
    }

    private static async Task<IResult> Deactivate(Guid userId, HttpContext http, IPortalAccountService service, IPortalAccountRepository accounts, CancellationToken ct)
        => await SimpleAction(userId, await service.SetActiveAsync(userId, false, PortalRequest.Actor(http), ct), accounts, ct,
            "not_active", "Only an active account can be deactivated.");

    private static async Task<IResult> Reactivate(Guid userId, HttpContext http, IPortalAccountService service, IPortalAccountRepository accounts, CancellationToken ct)
        => await SimpleAction(userId, await service.SetActiveAsync(userId, true, PortalRequest.Actor(http), ct), accounts, ct,
            "not_deactivated", "Only a deactivated account can be reactivated.");

    private static async Task<IResult> ResetTotp(Guid userId, HttpContext http, IPortalAccountService service, IPortalAccountRepository accounts, CancellationToken ct)
        => await SimpleAction(userId, await service.ResetTotpAsync(userId, PortalRequest.Actor(http), ct), accounts, ct,
            "not_found", "Unknown account.");

    private static async Task<IResult> RevokeSessions(Guid userId, HttpContext http, IPortalAccountService service, IPortalAccountRepository accounts, CancellationToken ct)
        => await SimpleAction(userId, await service.RevokeSessionsAsync(userId, PortalRequest.Actor(http), ct), accounts, ct,
            "not_found", "Unknown account.");

    private static async Task<IResult> ResendVerification(Guid userId, HttpContext http, IPortalAccountService service, IPortalAccountRepository accounts, CancellationToken ct)
        => await SimpleAction(userId, await service.ResendVerificationAsync(userId, PortalRequest.Actor(http), ct), accounts, ct,
            "cannot_resend", "The verification mail could not be sent (account not awaiting verification, or mail is not configured).");

    private static async Task<IResult> SimpleAction(Guid userId, bool ok, IPortalAccountRepository accounts, CancellationToken ct, string error, string message)
    {
        var row = await accounts.GetByUserIdAsync(userId, ct);
        if (row is null) return Results.NotFound();
        if (!ok) return Results.Conflict(new { error, message });
        return Results.Ok(new { account = Project(row) });
    }

    private static async Task<IResult> Delete(Guid userId, HttpContext http, IPortalAccountService service, CancellationToken ct)
    {
        var ok = await service.DeleteAsync(userId, PortalRequest.Actor(http), ct);
        return ok ? Results.NoContent() : Results.NotFound();
    }

    // ---- invitations ------------------------------------------------------

    private static async Task<IResult> ListInvitations([FromQuery] Guid? contactId, [FromQuery] bool? includeExpired, IPortalAccountRepository accounts, CancellationToken ct)
    {
        var rows = await accounts.ListInvitationsAsync(contactId, includeExpired ?? false, ct);
        return Results.Ok(rows.Select(Project));
    }

    public sealed record InviteRequest(string? Email, string? DisplayName, Guid? ContactId, Guid? CompanyId, string? CompanyRole, List<CompanyLinkDto>? Companies);

    private static async Task<IResult> Invite([FromBody] InviteRequest req, HttpContext http, IPortalAccountService service, CancellationToken ct)
    {
        var result = await service.InviteAsync(
            req.Email ?? string.Empty, req.DisplayName ?? string.Empty, req.ContactId,
            ToLinks(req.CompanyId, req.CompanyRole, req.Companies), PortalRequest.Actor(http), ct);
        return result.Outcome switch
        {
            InviteOutcome.Sent => Results.Ok(new { invitationId = result.InvitationId }),
            InviteOutcome.PortalDisabled => Results.Conflict(new { error = "portal_disabled", message = "Enable the portal first (Settings → Portal)." }),
            InviteOutcome.InvalidEmail => Results.BadRequest(new { error = "invalid_email", message = "Enter a valid email address." }),
            InviteOutcome.InvalidRole => Results.BadRequest(new { error = "invalid_role", message = "Role must be Member or TicketManager." }),
            InviteOutcome.EmailTaken => Results.Conflict(new { error = "email_taken", message = "A user with this email address already exists." }),
            InviteOutcome.ContactNotFound => Results.NotFound(new { error = "contact_not_found" }),
            InviteOutcome.ContactHasAccount => Results.Conflict(new { error = "contact_has_account", message = "This contact already has a portal account." }),
            InviteOutcome.MailFailed => Results.Json(new { error = "mail_failed", message = "The invitation mail could not be sent. Check the portal sender mailbox (Settings → Portal)." }, statusCode: StatusCodes.Status503ServiceUnavailable),
            _ => Results.StatusCode(StatusCodes.Status500InternalServerError),
        };
    }

    public sealed record SetRoleRequest([property: Required] string Role);

    private static async Task<IResult> SetPortalRole(Guid contactId, Guid companyId, [FromBody] SetRoleRequest req, HttpContext http,
        IPortalAccountService service, CancellationToken ct)
    {
        if (req.Role is not ("Member" or "TicketManager"))
            return Results.BadRequest(new { error = "invalid_role", message = "Role must be Member or TicketManager." });
        var ok = await service.SetPortalRoleAsync(contactId, companyId, req.Role, PortalRequest.Actor(http), ct);
        return ok ? Results.NoContent() : Results.NotFound(new { error = "link_not_found", message = "This contact is not linked to that company." });
    }

    private static async Task<IResult> ResendInvitation(Guid id, HttpContext http, IPortalAccountService service, CancellationToken ct)
    {
        var ok = await service.ResendInvitationAsync(id, PortalRequest.Actor(http), ct);
        return ok ? Results.Ok(new { ok = true })
                  : Results.Conflict(new { error = "cannot_resend", message = "This invitation cannot be re-sent (already used, or mail is not configured)." });
    }

    private static async Task<IResult> RevokeInvitation(Guid id, HttpContext http, IPortalAccountService service, CancellationToken ct)
    {
        var ok = await service.RevokeInvitationAsync(id, PortalRequest.Actor(http), ct);
        return ok ? Results.NoContent() : Results.NotFound();
    }

    // ---- Turnstile secret -------------------------------------------------

    private static async Task<IResult> GetTurnstileSecretStatus(IProtectedSecretStore secrets, CancellationToken ct) =>
        Results.Ok(new { configured = await secrets.HasAsync(ProtectedSecretKeys.PortalTurnstileSecret, ct) });

    public sealed record SetSecretRequest([property: Required] string Value);

    private static async Task<IResult> SetTurnstileSecret([FromBody] SetSecretRequest req, HttpContext http,
        IProtectedSecretStore secrets, IAuditLogger audit, CancellationToken ct)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.Value))
            return Results.BadRequest(new { error = "missing_value", message = "Secret key is required." });
        var trimmed = req.Value.Trim();
        if (trimmed.Length > 512)
            return Results.BadRequest(new { error = "invalid_key", message = "That does not look like a Turnstile secret key." });
        await secrets.SetAsync(ProtectedSecretKeys.PortalTurnstileSecret, trimmed, ct);
        var actor = PortalRequest.Actor(http);
        await audit.LogAsync(new AuditEvent(PortalEventTypes.TurnstileSecretUpdated, actor.Email, actor.Role,
            Target: ProtectedSecretKeys.PortalTurnstileSecret, ClientIp: actor.Ip, UserAgent: actor.UserAgent,
            Payload: new { configured = true }), ct);
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteTurnstileSecret(HttpContext http, IProtectedSecretStore secrets, IAuditLogger audit, CancellationToken ct)
    {
        await secrets.DeleteAsync(ProtectedSecretKeys.PortalTurnstileSecret, ct);
        var actor = PortalRequest.Actor(http);
        await audit.LogAsync(new AuditEvent(PortalEventTypes.TurnstileSecretDeleted, actor.Email, actor.Role,
            Target: ProtectedSecretKeys.PortalTurnstileSecret, ClientIp: actor.Ip, UserAgent: actor.UserAgent,
            Payload: new { configured = false }), ct);
        return Results.NoContent();
    }
}
