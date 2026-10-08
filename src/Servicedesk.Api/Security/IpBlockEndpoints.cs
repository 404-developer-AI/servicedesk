using System.Text.Json;
using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Security.IpBlocking;

namespace Servicedesk.Api.Security;

/// Admin surface for IP block proposals and rules. Thin by design: every
/// safeguard (own IP, protected networks, known-login confirmation) and every
/// audit row lives in <see cref="IIpBlockService"/>.
public static class IpBlockEndpoints
{
    public static IEndpointRouteBuilder MapIpBlockEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/security/ip-blocking")
            .WithTags("Security")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        group.MapGet("/overview", async (HttpContext http, IIpBlockRepository repo, IIpBlockList list,
            IIpThreatDetector detector, CancellationToken ct) =>
        {
            var rules = await repo.ListActiveRulesAsync(ct);
            var open = await repo.ListProposalsAsync(IpProposalStatus.Open, 200, ct);
            var recent = await repo.ListProposalsAsync(null, 100, ct);
            return Results.Ok(new
            {
                enabled = detector.Config.Enabled,
                yourIp = CurrentIp(http),
                openProposals = open.Select(Project),
                recentProposals = recent.Where(p => p.Status != IpProposalStatus.Open).Select(Project),
                rules = rules.Select(r => new
                {
                    ip = r.Ip,
                    kind = r.Kind,
                    source = r.Source,
                    reason = r.Reason,
                    proposalId = r.ProposalId,
                    createdUtc = r.CreatedUtc,
                    createdBy = r.CreatedBy,
                    expiresUtc = r.ExpiresUtc,
                    hitCount = r.HitCount,
                    lastHitUtc = r.LastHitUtc,
                }),
            });
        }).WithName("GetIpBlockingOverview").WithOpenApi();

        group.MapPost("/proposals/{id:long}/block", async (long id, DecisionRequest? req, HttpContext http,
            IIpBlockService service, CancellationToken ct) =>
        {
            var (outcome, known) = await service.BlockProposalAsync(id, req?.Reason, req?.ConfirmKnownLogins == true, Actor(http), ct);
            return ToResult(outcome, known);
        }).WithName("BlockIpProposal").WithOpenApi();

        group.MapPost("/proposals/{id:long}/whitelist", async (long id, DecisionRequest? req, HttpContext http,
            IIpBlockService service, CancellationToken ct) =>
            ToResult(await service.WhitelistProposalAsync(id, req?.Reason, Actor(http), ct), 0))
            .WithName("WhitelistIpProposal").WithOpenApi();

        group.MapPost("/proposals/{id:long}/dismiss", async (long id, HttpContext http,
            IIpBlockService service, CancellationToken ct) =>
            ToResult(await service.DismissProposalAsync(id, Actor(http), ct), 0))
            .WithName("DismissIpProposal").WithOpenApi();

        group.MapPost("/rules", async (AddRuleRequest req, HttpContext http, IIpBlockService service, CancellationToken ct) =>
        {
            if (req is null || string.IsNullOrWhiteSpace(req.Ip) || req.Ip.Length > 64)
                return Results.BadRequest(new { error = "A single IPv4 or IPv6 address is required." });
            var (outcome, known) = await service.AddRuleAsync(req.Ip, req.Kind ?? string.Empty, req.Reason,
                req.ConfirmKnownLogins == true, Actor(http), ct);
            return ToResult(outcome, known);
        }).WithName("AddIpRule").WithOpenApi();

        group.MapDelete("/rules/{ip}", async (string ip, HttpContext http, IIpBlockService service, CancellationToken ct) =>
            ToResult(await service.RemoveRuleAsync(ip, Actor(http), ct), 0))
            .WithName("RemoveIpRule").WithOpenApi();

        return app;
    }

    public sealed record DecisionRequest(string? Reason, bool? ConfirmKnownLogins);

    public sealed record AddRuleRequest(string Ip, string? Kind, string? Reason, bool? ConfirmKnownLogins);

    private static string? CurrentIp(HttpContext http) =>
        http.Connection.RemoteIpAddress is { } a ? IpAddressText.Normalize(a) : null;

    private static IpActor Actor(HttpContext http)
    {
        var (actor, role) = ActorContext.Resolve(http);
        return new IpActor(actor, role, CurrentIp(http), http.Request.Headers.UserAgent.ToString());
    }

    private static IResult ToResult(IpBlockOutcome outcome, int knownLogins) => outcome switch
    {
        IpBlockOutcome.Ok => Results.NoContent(),
        IpBlockOutcome.NotFound => Results.NotFound(),
        IpBlockOutcome.AlreadyDecided => Results.Conflict(new { error = "This proposal was already decided by someone else." }),
        IpBlockOutcome.InvalidIp => Results.BadRequest(new { error = "A single IPv4 or IPv6 address and a kind (block or whitelist) are required." }),
        IpBlockOutcome.ProtectedIp => Results.Conflict(new { error = "This address belongs to the server or its reverse proxy and can't be blocked." }),
        IpBlockOutcome.OwnIp => Results.Conflict(new { error = "You are connected from this address — blocking it would lock you out." }),
        IpBlockOutcome.KnownLoginsNeedConfirmation => Results.Conflict(new
        {
            error = $"{knownLogins} successful sign-in(s) came from this address. Blocking it will lock out everyone working from it.",
            code = "known_logins",
            knownLogins,
        }),
        _ => Results.StatusCode(StatusCodes.Status500InternalServerError),
    };

    private static object Project(IpProposal p)
    {
        JsonElement evidence;
        try { evidence = JsonDocument.Parse(p.EvidenceJson).RootElement.Clone(); }
        catch { evidence = JsonDocument.Parse("{}").RootElement.Clone(); }
        return new
        {
            id = p.Id,
            ip = p.Ip,
            status = p.Status,
            autoBlocked = p.AutoBlocked,
            reasons = p.Reasons,
            evidence,
            knownLogin = p.KnownLogin,
            firstSeenUtc = p.FirstSeenUtc,
            lastSeenUtc = p.LastSeenUtc,
            createdUtc = p.CreatedUtc,
            decidedUtc = p.DecidedUtc,
            decidedBy = p.DecidedBy,
        };
    }
}
