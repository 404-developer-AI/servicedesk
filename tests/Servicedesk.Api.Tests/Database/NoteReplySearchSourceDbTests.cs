using Servicedesk.Domain.Search;
using Servicedesk.Infrastructure.Search;
using Servicedesk.Infrastructure.Tickets.NoteThreads;
using Xunit;

namespace Servicedesk.Api.Tests.Database;

/// v0.1.54 — replies are found only by agents with access to the ticket's
/// queue; deleted replies never match.
[Collection(PostgresCollection.Name)]
public sealed class NoteReplySearchSourceDbTests
{
    private readonly PostgresDatabase _db;

    public NoteReplySearchSourceDbTests(PostgresDatabase db) => _db = db;

    [PostgresFact]
    public async Task Agents_find_replies_only_in_their_queues_and_never_deleted_ones()
    {
        var token = $"walrus{Guid.NewGuid():N}"[..14];
        var seed = new DbSeed(_db.DataSource);
        var (queueA, s, p) = await seed.TaxonomyAsync();
        var queueB = await seed.QueueAsync("Other");
        var contact = await seed.ContactAsync();
        var author = await seed.UserAsync("Agent");
        var inA = await seed.TicketAsync("A", contact, queueA, s, p);
        var inB = await seed.TicketAsync("B", contact, queueB, s, p);
        var noteA = await seed.EventAsync(inA, "Note", true, authorUserId: author);
        var noteB = await seed.EventAsync(inB, "Note", true, authorUserId: author);
        var repo = new NoteThreadRepository(_db.DataSource);
        await repo.CreateReplyAsync(noteA, null, 1, author, $"<p>{token} ok</p>", $"{token} ok", new { }, default);
        await repo.CreateReplyAsync(noteB, null, 1, author, $"<p>{token} other</p>", $"{token} other", new { }, default);
        var gone = await repo.CreateReplyAsync(noteA, null, 1, author, $"<p>{token} gone</p>", $"{token} gone", new { }, default);
        await repo.SoftDeleteReplyAsync(gone, author, default);

        var src = new NoteReplySearchSource(_db.DataSource);
        var hits = await src.SearchAsync(new SearchRequest(token, null, 20, 0),
            new SearchPrincipal(Guid.NewGuid(), "Agent", new[] { queueA }), default);

        var hit = Assert.Single(hits.Hits);
        Assert.Equal(inA.ToString(), hit.EntityId);
    }
}
