using System.Text.Json.Nodes;
using Servicedesk.Domain.Search;
using Servicedesk.Infrastructure.Forms;
using Servicedesk.Infrastructure.Search;
using Xunit;

namespace Servicedesk.Api.Tests.Database;

/// v0.1.54 — the Back office forms search source follows the per-user flag
/// (admins included) and, like the work list, is not queue-scoped: a flag
/// holder without queue access finds marked forms, never unmarked ones.
[Collection(PostgresCollection.Name)]
public sealed class BackofficeFormSearchSourceDbTests
{
    private readonly PostgresDatabase _db;

    public BackofficeFormSearchSourceDbTests(PostgresDatabase db) => _db = db;

    private static readonly IReadOnlySet<string> Flag = new HashSet<string> { SearchFeature.BackofficeForms };

    private static SearchRequest Query(string q) => new(q, null, 20, 0);

    [PostgresFact]
    public async Task Flag_holder_finds_marked_forms_regardless_of_queue_access()
    {
        var token = $"kiwi{Guid.NewGuid():N}"[..13];
        var seed = new DbSeed(_db.DataSource);
        var (q, s, p) = await seed.TaxonomyAsync();
        var ticket = await seed.TicketAsync("Licence request", await seed.ContactAsync(), q, s, p);
        var agent = await seed.UserAsync("Agent");
        var forms = new TicketFormRepository(_db.DataSource);
        var def = new FormDefinition();
        var marked = await forms.CreateAsync(ticket, null, $"{token} licences", def, new JsonObject(), "", agent, true, default);
        await forms.CreateAsync(ticket, null, $"{token} checklist", def, new JsonObject(), "", agent, false, default);

        var source = new BackofficeFormSearchSource(_db.DataSource);
        var noQueues = new SearchPrincipal(Guid.NewGuid(), "Agent", Array.Empty<Guid>(), Flag);

        var hits = await source.SearchAsync(Query(token), noQueues, default);

        Assert.Equal(new[] { marked.ToString() }, hits.Hits.Select(h => h.EntityId).ToArray());
    }

    [Theory]
    [InlineData("Agent")]
    [InlineData("Admin")]
    [InlineData("Customer")]
    public async Task Without_the_flag_nobody_gets_hits(string role)
    {
        // null! data source: an unavailable principal must never reach the DB.
        var source = new BackofficeFormSearchSource(null!);
        var principal = new SearchPrincipal(Guid.NewGuid(), role, allowedQueueIds: null);

        Assert.False(source.IsAvailableFor(principal));
        Assert.Empty((await source.SearchAsync(Query("licence"), principal, default)).Hits);
    }

    [Fact]
    public async Task A_customer_with_the_flag_still_gets_nothing()
    {
        var source = new BackofficeFormSearchSource(null!);
        var customer = new SearchPrincipal(Guid.NewGuid(), "Customer", Array.Empty<Guid>(), Flag);
        Assert.Empty((await source.SearchAsync(Query("licence"), customer, default)).Hits);
    }
}
