using System.Text.Json;
using Servicedesk.Infrastructure.Audit;

namespace Servicedesk.Infrastructure.Security.IpBlocking;

public enum IpBlockOutcome
{
    Ok,
    NotFound,
    AlreadyDecided,
    InvalidIp,
    /// Loopback / reverse-proxy network — blocking it would lock out everyone.
    ProtectedIp,
    /// The admin's own current address.
    OwnIp,
    /// Successful sign-ins came from this IP; the caller must confirm.
    KnownLoginsNeedConfirmation,
}

public sealed record IpActor(string Name, string Role, string? CurrentIp, string? UserAgent);

/// Every admin decision and every automatic action goes through here, so the
/// safeguards (own IP, protected networks, known-login confirmation) and the
/// audit trail are identical for the endpoints and the background worker.
public interface IIpBlockService
{
    Task<(IpBlockOutcome Outcome, int KnownLogins)> BlockProposalAsync(long proposalId, string? reason, bool confirmKnownLogins, IpActor actor, CancellationToken ct);
    Task<IpBlockOutcome> WhitelistProposalAsync(long proposalId, string? reason, IpActor actor, CancellationToken ct);
    Task<IpBlockOutcome> DismissProposalAsync(long proposalId, IpActor actor, CancellationToken ct);
    Task<(IpBlockOutcome Outcome, int KnownLogins)> AddRuleAsync(string ipText, string kind, string? reason, bool confirmKnownLogins, IpActor actor, CancellationToken ct);
    Task<IpBlockOutcome> RemoveRuleAsync(string ipText, IpActor actor, CancellationToken ct);

    /// Worker path: raise/refresh the proposal and, for an unambiguous
    /// scanner, apply the temporary block. Returns the proposal id when a NEW
    /// proposal was created (so the caller alerts admins once).
    Task<(long? NewProposalId, bool AutoBlocked)> HandleObservationAsync(IpThreatObservation observation, CancellationToken ct);

    Task RefreshOpenCountAsync(CancellationToken ct);
}

public sealed class IpBlockService : IIpBlockService
{
    internal const string AuditAutoBlocked = "security.ip.auto_blocked";
    internal const string AuditProposalRaised = "security.ip.proposal_raised";
    internal const string AuditBlocked = "security.ip.blocked";
    internal const string AuditWhitelisted = "security.ip.whitelisted";
    internal const string AuditDismissed = "security.ip.proposal_dismissed";
    internal const string AuditRuleRemoved = "security.ip.rule_removed";

    private const int MaxReasonLength = 500;

    private readonly IIpBlockRepository _repo;
    private readonly IIpBlockList _list;
    private readonly IIpThreatDetector _detector;
    private readonly IAuditLogger _audit;
    private readonly TimeProvider _clock;

    public IpBlockService(IIpBlockRepository repo, IIpBlockList list, IIpThreatDetector detector,
        IAuditLogger audit, TimeProvider? clock = null)
    {
        _repo = repo;
        _list = list;
        _detector = detector;
        _audit = audit;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<(IpBlockOutcome, int)> BlockProposalAsync(long proposalId, string? reason, bool confirmKnownLogins, IpActor actor, CancellationToken ct)
    {
        var p = await _repo.GetProposalAsync(proposalId, ct);
        if (p is null) return (IpBlockOutcome.NotFound, 0);
        if (p.Status != IpProposalStatus.Open) return (IpBlockOutcome.AlreadyDecided, 0);

        var guard = await GuardBlockAsync(p.Ip, confirmKnownLogins, actor, ct);
        if (guard.Outcome != IpBlockOutcome.Ok) return guard;
        if (!await _repo.DecideProposalAsync(p.Id, IpProposalStatus.Blocked, actor.Name, ct))
            return (IpBlockOutcome.AlreadyDecided, 0);

        // Permanent until an admin lifts it (agreed default).
        await _repo.UpsertRuleAsync(p.Ip, IpRuleKind.Block, "admin", Trim(reason) ?? DefaultReason(p), p.Id, actor.Name, null, ct);
        _list.ApplyRule(p.Ip, IpRuleKind.Block, null);
        await AuditAsync(AuditBlocked, actor, p.Ip, new { proposalId = p.Id, wasAutoBlocked = p.AutoBlocked, knownLogins = guard.KnownLogins }, ct);
        await RefreshOpenCountAsync(ct);
        return (IpBlockOutcome.Ok, guard.KnownLogins);
    }

    public async Task<IpBlockOutcome> WhitelistProposalAsync(long proposalId, string? reason, IpActor actor, CancellationToken ct)
    {
        var p = await _repo.GetProposalAsync(proposalId, ct);
        if (p is null) return IpBlockOutcome.NotFound;
        if (p.Status != IpProposalStatus.Open) return IpBlockOutcome.AlreadyDecided;
        if (!await _repo.DecideProposalAsync(p.Id, IpProposalStatus.Whitelisted, actor.Name, ct))
            return IpBlockOutcome.AlreadyDecided;

        await _repo.UpsertRuleAsync(p.Ip, IpRuleKind.Whitelist, "admin", Trim(reason), p.Id, actor.Name, null, ct);
        _list.ApplyRule(p.Ip, IpRuleKind.Whitelist, null);
        await AuditAsync(AuditWhitelisted, actor, p.Ip, new { proposalId = p.Id, liftedAutoBlock = p.AutoBlocked }, ct);
        await RefreshOpenCountAsync(ct);
        return IpBlockOutcome.Ok;
    }

    public async Task<IpBlockOutcome> DismissProposalAsync(long proposalId, IpActor actor, CancellationToken ct)
    {
        var p = await _repo.GetProposalAsync(proposalId, ct);
        if (p is null) return IpBlockOutcome.NotFound;
        if (p.Status != IpProposalStatus.Open) return IpBlockOutcome.AlreadyDecided;
        if (!await _repo.DecideProposalAsync(p.Id, IpProposalStatus.Dismissed, actor.Name, ct))
            return IpBlockOutcome.AlreadyDecided;

        // Dismiss = "not a threat": a pending automatic block is released.
        // A permanent admin block on the same IP (set earlier by hand) stays.
        var rule = await _repo.GetRuleAsync(p.Ip, ct);
        var released = false;
        if (rule is { Kind: IpRuleKind.Block, Source: "auto" })
        {
            await _repo.DeleteRuleAsync(p.Ip, ct);
            _list.Remove(p.Ip);
            released = true;
        }
        await AuditAsync(AuditDismissed, actor, p.Ip, new { proposalId = p.Id, releasedAutoBlock = released }, ct);
        await RefreshOpenCountAsync(ct);
        return IpBlockOutcome.Ok;
    }

    public async Task<(IpBlockOutcome, int)> AddRuleAsync(string ipText, string kind, string? reason, bool confirmKnownLogins, IpActor actor, CancellationToken ct)
    {
        var ip = IpAddressText.TryNormalize(ipText);
        if (ip is null || (kind != IpRuleKind.Block && kind != IpRuleKind.Whitelist)) return (IpBlockOutcome.InvalidIp, 0);

        var known = 0;
        if (kind == IpRuleKind.Block)
        {
            var guard = await GuardBlockAsync(ip, confirmKnownLogins, actor, ct);
            if (guard.Outcome != IpBlockOutcome.Ok) return guard;
            known = guard.KnownLogins;
        }

        await _repo.UpsertRuleAsync(ip, kind, "admin", Trim(reason), null, actor.Name, null, ct);
        _list.ApplyRule(ip, kind, null);
        await _repo.CloseOpenProposalForIpAsync(ip,
            kind == IpRuleKind.Block ? IpProposalStatus.Blocked : IpProposalStatus.Whitelisted, actor.Name, ct);
        await AuditAsync(kind == IpRuleKind.Block ? AuditBlocked : AuditWhitelisted, actor, ip,
            new { manual = true, knownLogins = known }, ct);
        await RefreshOpenCountAsync(ct);
        return (IpBlockOutcome.Ok, known);
    }

    public async Task<IpBlockOutcome> RemoveRuleAsync(string ipText, IpActor actor, CancellationToken ct)
    {
        var ip = IpAddressText.TryNormalize(ipText);
        if (ip is null) return IpBlockOutcome.InvalidIp;
        var rule = await _repo.GetRuleAsync(ip, ct);
        if (rule is null) return IpBlockOutcome.NotFound;

        await _repo.DeleteRuleAsync(ip, ct);
        _list.Remove(ip);
        // An auto-block lifted by hand also settles its pending proposal.
        if (rule.Source == "auto")
            await _repo.CloseOpenProposalForIpAsync(ip, IpProposalStatus.Dismissed, actor.Name, ct);
        await AuditAsync(AuditRuleRemoved, actor, ip, new { kind = rule.Kind, source = rule.Source, rule.HitCount }, ct);
        await RefreshOpenCountAsync(ct);
        return IpBlockOutcome.Ok;
    }

    public async Task<(long?, bool)> HandleObservationAsync(IpThreatObservation o, CancellationToken ct)
    {
        if (_list.IsProtected(o.Ip) || _list.IsWhitelisted(o.Ip)) return (null, false);

        var existing = await _repo.GetRuleAsync(o.Ip, ct);
        if (existing is { Kind: IpRuleKind.Whitelist }) return (null, false);
        // Already blocked for good — nothing left to propose.
        if (existing is { Kind: IpRuleKind.Block, ExpiresUtc: null }) return (null, false);

        var knownLogins = await _repo.CountSuccessfulLoginsAsync(o.Ip, ct);
        var isScanner = o.TrippedKinds.Contains(IpSignalKind.ScannerPath);
        // Unambiguous scanner = scanner-path probes from an address that never
        // signed in. Anything else (incl. scanner hits from a known office IP)
        // is proposal-only.
        var autoBlock = isScanner && knownLogins == 0;

        var reasons = o.TrippedKinds.Select(ReasonKey).ToList();
        var evidence = JsonSerializer.Serialize(new
        {
            windowMinutes = (int)_detector.Config.Window.TotalMinutes,
            counts = o.Counts.ToDictionary(kv => ReasonKey(kv.Key), kv => kv.Value),
            samplePaths = o.SamplePaths,
        });

        var (proposalId, created) = await _repo.UpsertOpenProposalAsync(o, reasons, evidence, knownLogins > 0, autoBlock, ct);

        var system = new IpActor("system", "system", null, null);
        if (autoBlock)
        {
            var expires = _clock.GetUtcNow().UtcDateTime + _detector.Config.AutoBlockDuration;
            await _repo.UpsertRuleAsync(o.Ip, IpRuleKind.Block, "auto",
                "Automatic: vulnerability-scanner probes", proposalId, "system", expires, ct);
            _list.ApplyRule(o.Ip, IpRuleKind.Block, expires);
            await AuditAsync(AuditAutoBlocked, system, o.Ip,
                new { proposalId, expiresUtc = expires, samplePaths = o.SamplePaths }, ct);
        }
        else if (created)
        {
            await AuditAsync(AuditProposalRaised, system, o.Ip, new { proposalId, reasons, knownLogins }, ct);
        }

        await RefreshOpenCountAsync(ct);
        return (created ? proposalId : null, autoBlock);
    }

    public async Task RefreshOpenCountAsync(CancellationToken ct) =>
        _list.SetOpenProposalCount(await _repo.CountOpenProposalsAsync(ct));

    private async Task<(IpBlockOutcome Outcome, int KnownLogins)> GuardBlockAsync(string ip, bool confirmKnownLogins, IpActor actor, CancellationToken ct)
    {
        if (_list.IsProtected(ip)) return (IpBlockOutcome.ProtectedIp, 0);
        if (actor.CurrentIp is not null && string.Equals(IpAddressText.TryNormalize(actor.CurrentIp), ip, StringComparison.Ordinal))
            return (IpBlockOutcome.OwnIp, 0);
        var known = await _repo.CountSuccessfulLoginsAsync(ip, ct);
        if (known > 0 && !confirmKnownLogins) return (IpBlockOutcome.KnownLoginsNeedConfirmation, known);
        return (IpBlockOutcome.Ok, known);
    }

    private Task AuditAsync(string eventType, IpActor actor, string ip, object payload, CancellationToken ct) =>
        // ClientIp is the ADMIN's address (or null for the system), never the
        // subject IP — the subject lives in Target. Keeps the IP-signal
        // decorator from ever counting these rows against anyone.
        _audit.LogAsync(new AuditEvent(eventType, actor.Name, actor.Role, ip, actor.CurrentIp, actor.UserAgent, payload), ct);

    internal static string ReasonKey(IpSignalKind kind) => kind switch
    {
        IpSignalKind.ScannerPath => "scanner_paths",
        IpSignalKind.RateLimited => "rate_limited",
        IpSignalKind.CsrfRejected => "csrf_rejected",
        IpSignalKind.FailedLogin => "failed_logins",
        _ => kind.ToString(),
    };

    private static string DefaultReason(IpProposal p) =>
        p.Reasons.Count > 0 ? "Confirmed proposal: " + string.Join(", ", p.Reasons) : "Confirmed proposal";

    private static string? Trim(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return null;
        var r = reason.Trim();
        return r.Length > MaxReasonLength ? r[..MaxReasonLength] : r;
    }
}
