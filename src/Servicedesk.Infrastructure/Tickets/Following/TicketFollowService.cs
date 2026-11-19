using Servicedesk.Infrastructure.Access;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Tickets.Following;

public enum TicketFollowRejectCode { NotFound, LimitReached, InvalidNote }

public sealed class TicketFollowRejectedException : Exception
{
    public TicketFollowRejectCode Code { get; }
    public TicketFollowRejectedException(TicketFollowRejectCode code, string message) : base(message) => Code = code;
}

public interface ITicketFollowService
{
    Task<IReadOnlyList<FollowedTicket>> ListAsync(TicketMutationActor actor, CancellationToken ct);
    /// Follows the ticket. Needs queue access to it (404 otherwise); refused
    /// above Notifications.FollowingMaxTicketsPerUser. Idempotent.
    Task FollowAsync(TicketMutationActor actor, Guid ticketId, CancellationToken ct);
    /// Always allowed for the user's own row, also after losing access.
    Task<bool> UnfollowAsync(TicketMutationActor actor, Guid ticketId, CancellationToken ct);
    /// Clears the "news" marker. A no-op when the ticket is not followed.
    Task<bool> MarkSeenAsync(TicketMutationActor actor, Guid ticketId, CancellationToken ct);
    /// Sets the caller's personal note on a ticket they follow (empty clears
    /// it). Needs queue access; NotFound when not followed, InvalidNote when
    /// longer than <see cref="FollowNote.MaxLength"/>.
    Task SetNoteAsync(TicketMutationActor actor, Guid ticketId, string? note, CancellationToken ct);
}

/// v0.1.58 — the personal note on a follow: plain text, trimmed, line
/// breaks kept, other control characters dropped; empty means no note.
public static class FollowNote
{
    public const int MaxLength = 500;

    /// The stored form of <paramref name="raw"/>, or null for "no note".
    public static string? Normalize(string? raw)
    {
        if (raw is null) return null;
        var text = raw.Replace("\r\n", "\n").Replace('\r', '\n');
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (var ch in text)
            if (ch == '\n' || !char.IsControl(ch)) sb.Append(ch);
        var result = sb.ToString().Trim();
        return result.Length == 0 ? null : result;
    }
}

/// v0.1.54 — following tickets (see <see cref="TicketFollowRepository"/>).
/// Access goes through the shared mutation precheck (queue access); the
/// list is filtered by the caller's <see cref="QueueAccessScope"/>, so a
/// ticket the agent can no longer see silently drops out of list and count.
public sealed class TicketFollowService : ITicketFollowService
{
    public const int MinCap = 10;
    public const int MaxCap = 1000;

    private readonly ITicketFollowRepository _repo;
    private readonly ITicketMutationService _mutations;
    private readonly IQueueAccessService _queueAccess;
    private readonly ISettingsService _settings;

    public TicketFollowService(ITicketFollowRepository repo, ITicketMutationService mutations,
        IQueueAccessService queueAccess, ISettingsService settings)
    {
        _repo = repo;
        _mutations = mutations;
        _queueAccess = queueAccess;
        _settings = settings;
    }

    public async Task<IReadOnlyList<FollowedTicket>> ListAsync(TicketMutationActor actor, CancellationToken ct)
    {
        var scope = await _queueAccess.GetScopeAsync(actor.UserId, actor.Role, ct);
        return await _repo.ListAsync(actor.UserId, scope, ct);
    }

    public async Task FollowAsync(TicketMutationActor actor, Guid ticketId, CancellationToken ct)
    {
        var access = await _mutations.PrecheckAccessAsync(actor, ticketId, ct);
        if (access.Check != TicketMutationCheck.Ok || access.Ticket is null)
            throw new TicketFollowRejectedException(TicketFollowRejectCode.NotFound, "Not found.");

        var cap = Math.Clamp(await _settings.GetAsync<int>(SettingKeys.Notifications.FollowingMaxTicketsPerUser, ct), MinCap, MaxCap);
        var result = await _repo.FollowAsync(actor.UserId, ticketId, cap, ct);
        if (result == FollowResult.LimitReached)
            throw new TicketFollowRejectedException(TicketFollowRejectCode.LimitReached,
                $"You already follow {cap} tickets. Unfollow one before following another.");
    }

    public Task<bool> UnfollowAsync(TicketMutationActor actor, Guid ticketId, CancellationToken ct)
        => _repo.UnfollowAsync(actor.UserId, ticketId, ct);

    public Task<bool> MarkSeenAsync(TicketMutationActor actor, Guid ticketId, CancellationToken ct)
        => _repo.MarkSeenAsync(actor.UserId, ticketId, ct);

    public async Task SetNoteAsync(TicketMutationActor actor, Guid ticketId, string? note, CancellationToken ct)
    {
        var normalized = FollowNote.Normalize(note);
        if (normalized is { Length: > FollowNote.MaxLength })
            throw new TicketFollowRejectedException(TicketFollowRejectCode.InvalidNote,
                $"A follow note can be at most {FollowNote.MaxLength} characters.");

        var access = await _mutations.PrecheckAccessAsync(actor, ticketId, ct);
        if (access.Check != TicketMutationCheck.Ok || access.Ticket is null
            || !await _repo.SetNoteAsync(actor.UserId, ticketId, normalized, ct))
            throw new TicketFollowRejectedException(TicketFollowRejectCode.NotFound, "Not found.");
    }
}
