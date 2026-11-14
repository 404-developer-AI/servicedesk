using System.Text.Json.Nodes;
using Dapper;
using Servicedesk.Infrastructure.Forms;
using Xunit;

namespace Servicedesk.Api.Tests.Database;

/// v0.1.54 — the Back office forms lifecycle against a real database: the
/// marking is copied onto the form at fill time, every transition is one
/// guarded UPDATE (two back-office users can never both win), an edit after
/// handling puts the form back on the list, and removed forms / deleted
/// tickets never show up.
[Collection(PostgresCollection.Name)]
public sealed class BackofficeFormDbTests
{
    private readonly PostgresDatabase _db;

    public BackofficeFormDbTests(PostgresDatabase db) => _db = db;

    private static readonly Guid FieldId = Guid.Parse("6f1d3a52-0c4e-4d7b-9a51-2b8e7c1f0a01");

    private static FormDefinition Def() => new()
    {
        Fields = { new FormField { Id = FieldId, Type = FormFieldType.ShortText, Label = "Licences activated" } },
    };

    private static JsonObject Answers(string v) => new() { [FieldId.ToString()] = v };

    private async Task<(Guid Ticket, Guid Agent, Guid Other)> SeedAsync(bool deletedTicket = false)
    {
        var seed = new DbSeed(_db.DataSource);
        var (q, s, p) = await seed.TaxonomyAsync();
        var ticket = await seed.TicketAsync("Licence request", await seed.ContactAsync(), q, s, p, isDeleted: deletedTicket);
        return (ticket, await seed.UserAsync("Agent"), await seed.UserAsync("Agent"));
    }

    private async Task<Guid> FillAsync(Guid ticket, Guid agent, bool sendToBackoffice, string name = "Licence request")
        => await new TicketFormRepository(_db.DataSource).CreateAsync(
            ticket, null, $"{name} {Guid.NewGuid():N}", Def(), Answers("5"), "Licences activated: 5",
            agent, sendToBackoffice, default);

    [PostgresFact]
    public async Task Marked_form_starts_open_and_unmarked_form_never_lists()
    {
        var (ticket, agent, _) = await SeedAsync();
        var bo = new BackofficeFormRepository(_db.DataSource);
        var before = await bo.CountsAsync(default);

        var marked = await FillAsync(ticket, agent, sendToBackoffice: true);
        var plain = await FillAsync(ticket, agent, sendToBackoffice: false);

        Assert.Equal(BackofficeFormStatus.Open, (await bo.GetItemAsync(marked, default))!.Status);
        Assert.Null(await bo.GetItemAsync(plain, default));
        Assert.Equal(before.Open + 1, (await bo.CountsAsync(default)).Open);

        var log = (await bo.GetDetailAsync(marked, default))!.Log;
        Assert.Equal(BackofficeFormAction.Submitted, Assert.Single(log).Action);
    }

    [PostgresFact]
    public async Task Two_concurrent_claims_have_exactly_one_winner()
    {
        var (ticket, agent, other) = await SeedAsync();
        var form = await FillAsync(ticket, agent, sendToBackoffice: true);
        var bo = new BackofficeFormRepository(_db.DataSource);

        var results = await Task.WhenAll(
            bo.ClaimAsync(form, agent, takeOver: false, default),
            bo.ClaimAsync(form, other, takeOver: false, default));

        Assert.Single(results, r => r is not null);
        var item = (await bo.GetItemAsync(form, default))!;
        Assert.Equal(BackofficeFormStatus.InProgress, item.Status);
        Assert.Contains(item.ClaimedByUserId!.Value, new[] { agent, other });
    }

    [PostgresFact]
    public async Task Only_the_claimer_handles_or_releases_unless_forced_or_taken_over()
    {
        var (ticket, agent, other) = await SeedAsync();
        var form = await FillAsync(ticket, agent, sendToBackoffice: true);
        var bo = new BackofficeFormRepository(_db.DataSource);
        Assert.NotNull(await bo.ClaimAsync(form, agent, takeOver: false, default));

        Assert.Null(await bo.ClaimAsync(form, other, takeOver: false, default));
        Assert.Null(await bo.HandleAsync(form, other, "done", force: false, default));
        Assert.Null(await bo.ReleaseAsync(form, other, force: false, default));

        Assert.NotNull(await bo.ClaimAsync(form, other, takeOver: true, default));
        Assert.Equal(other, (await bo.GetItemAsync(form, default))!.ClaimedByUserId);

        Assert.NotNull(await bo.HandleAsync(form, other, "Contract CR-1001 updated", force: false, default));
        var handled = (await bo.GetItemAsync(form, default))!;
        Assert.Equal(BackofficeFormStatus.Handled, handled.Status);
        Assert.Equal(other, handled.HandledByUserId);
        Assert.Equal("Contract CR-1001 updated", handled.HandledNote);
        Assert.Equal(1, handled.HandledVersion);
        Assert.Null(handled.ClaimedByUserId);

        // A second handle on a handled form matches nothing.
        Assert.Null(await bo.HandleAsync(form, agent, "again", force: true, default));
    }

    [PostgresFact]
    public async Task Edit_after_handling_puts_the_form_back_on_the_list()
    {
        var (ticket, agent, other) = await SeedAsync();
        var form = await FillAsync(ticket, agent, sendToBackoffice: true);
        var bo = new BackofficeFormRepository(_db.DataSource);
        Assert.NotNull(await bo.HandleAsync(form, other, "Checked", force: false, default));

        var update = await new TicketFormRepository(_db.DataSource).UpdateAsync(
            form, 1, Answers("7"), "Licences activated: 7", agent, default);

        Assert.NotNull(update);
        Assert.Equal(2, update!.Version);
        Assert.Equal(BackofficeFormStatus.Handled, update.PreviousBoStatus);

        var detail = (await bo.GetDetailAsync(form, default))!;
        Assert.Equal(BackofficeFormStatus.Open, detail.Item.Status);
        Assert.True(detail.Item.Reopened);
        Assert.Equal("5", detail.HandledAnswers![FieldId.ToString()]!.GetValue<string>());
        Assert.Equal("7", detail.Answers[FieldId.ToString()]!.GetValue<string>());
        Assert.Equal(BackofficeFormAction.Reopened, detail.Log[0].Action);

        // Handling again clears the "changed after handling" flag.
        Assert.NotNull(await bo.HandleAsync(form, other, "Rechecked", force: false, default));
        Assert.False((await bo.GetItemAsync(form, default))!.Reopened);
    }

    [PostgresFact]
    public async Task Edit_while_in_progress_keeps_the_claim()
    {
        var (ticket, agent, other) = await SeedAsync();
        var form = await FillAsync(ticket, agent, sendToBackoffice: true);
        var bo = new BackofficeFormRepository(_db.DataSource);
        Assert.NotNull(await bo.ClaimAsync(form, other, takeOver: false, default));

        var update = await new TicketFormRepository(_db.DataSource).UpdateAsync(
            form, 1, Answers("6"), "Licences activated: 6", agent, default);

        Assert.Equal(BackofficeFormStatus.InProgress, update!.PreviousBoStatus);
        var item = (await bo.GetItemAsync(form, default))!;
        Assert.Equal(BackofficeFormStatus.InProgress, item.Status);
        Assert.Equal(other, item.ClaimedByUserId);
        Assert.Equal(1, item.ClaimedVersion);
        Assert.Equal(2, item.Version);
    }

    [PostgresFact]
    public async Task Removed_forms_and_deleted_tickets_never_list()
    {
        var bo = new BackofficeFormRepository(_db.DataSource);
        var (ticket, agent, _) = await SeedAsync();
        var removed = await FillAsync(ticket, agent, sendToBackoffice: true);
        await new TicketFormRepository(_db.DataSource).SoftDeleteAsync(removed, agent, "Filled in by mistake", default);

        var (deletedTicket, agent2, _) = await SeedAsync(deletedTicket: true);
        var onDeleted = await FillAsync(deletedTicket, agent2, sendToBackoffice: true);

        Assert.Null(await bo.GetItemAsync(removed, default));
        Assert.Null(await bo.GetItemAsync(onDeleted, default));
        var page = await bo.ListAsync(new BackofficeFormListQuery(null, null, null, null, null, 0, 200), default);
        Assert.DoesNotContain(page.Items, i => i.Id == removed || i.Id == onDeleted);
        Assert.Null(await bo.ClaimAsync(removed, agent, takeOver: false, default));
    }

    [PostgresFact]
    public async Task List_filters_by_status_and_search_and_pages()
    {
        var (ticket, agent, other) = await SeedAsync();
        var bo = new BackofficeFormRepository(_db.DataSource);
        var tag = $"Sophos{Guid.NewGuid():N}"[..14];
        var a = await FillAsync(ticket, agent, true, tag);
        var b = await FillAsync(ticket, agent, true, tag);
        await bo.HandleAsync(b, other, "ok", force: false, default);

        var open = await bo.ListAsync(new BackofficeFormListQuery(BackofficeFormStatus.Open, null, null, null, tag, 0, 50), default);
        Assert.Equal(new[] { a }, open.Items.Select(i => i.Id));
        Assert.Equal(1, open.Total);

        var all = await bo.ListAsync(new BackofficeFormListQuery(null, null, null, agent, tag, 0, 1), default);
        Assert.Single(all.Items);
        Assert.Equal(2, all.Total);

        await using var c = await _db.DataSource.OpenConnectionAsync();
        var number = await c.ExecuteScalarAsync<long>("SELECT number FROM tickets WHERE id = @ticket", new { ticket });
        var byNumber = await bo.ListAsync(new BackofficeFormListQuery(null, null, null, null, $"#{number}", 0, 50), default);
        Assert.Contains(byNumber.Items, i => i.Id == a);
    }
}
