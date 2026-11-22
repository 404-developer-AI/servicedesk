using System.Text.Json.Nodes;
using Dapper;
using Servicedesk.Infrastructure.Forms;
using Xunit;

namespace Servicedesk.Api.Tests.Database;

/// The Back office forms lifecycle against a real database: the marking is
/// copied onto the form at fill time, every transition is one guarded UPDATE
/// (two back-office users can never both win), an edit after handling puts
/// the form back on the list, removed forms / deleted tickets never show up.
/// v0.1.61 — Open → Handled without a claim step; "waiting for customer" is
/// derived from the ticket's status category; the trigger action handles the
/// open forms of chosen templates on one ticket.
[Collection(PostgresCollection.Name)]
public sealed class BackofficeFormDbTests
{
    private readonly PostgresDatabase _db;

    public BackofficeFormDbTests(PostgresDatabase db) => _db = db;

    private static readonly Guid FieldId = Guid.Parse("6f1d3a52-0c4e-4d7b-9a51-2b8e7c1f0a01");
    private static readonly IReadOnlyList<string> Waiting = new[] { "Pending" };

    private static FormDefinition Def() => new()
    {
        Fields = { new FormField { Id = FieldId, Type = FormFieldType.ShortText, Label = "Licences activated" } },
    };

    private static JsonObject Answers(string v) => new() { [FieldId.ToString()] = v };

    private async Task<(Guid Ticket, Guid Agent, Guid Other)> SeedAsync(bool deletedTicket = false, string stateCategory = "Open")
    {
        var seed = new DbSeed(_db.DataSource);
        var (q, _, p) = await seed.TaxonomyAsync();
        var s = await seed.StatusAsync(stateCategory);
        var ticket = await seed.TicketAsync("Licence request", await seed.ContactAsync(), q, s, p, isDeleted: deletedTicket);
        return (ticket, await seed.UserAsync("Agent"), await seed.UserAsync("Agent"));
    }

    private async Task<Guid> TemplateAsync()
    {
        await using var c = await _db.DataSource.OpenConnectionAsync();
        return await c.ExecuteScalarAsync<Guid>("""
            INSERT INTO form_templates (name, definition, field_count, send_to_backoffice, is_active)
            VALUES (@name, @def::jsonb, 1, TRUE, FALSE) RETURNING id
            """, new { name = $"Intervention request {Guid.NewGuid():N}", def = Def().ToJson() });
    }

    private async Task<Guid> FillAsync(Guid ticket, Guid agent, bool sendToBackoffice, string name = "Licence request", Guid? templateId = null)
        => await new TicketFormRepository(_db.DataSource).CreateAsync(
            ticket, templateId, $"{name} {Guid.NewGuid():N}", Def(), Answers("5"), "Licences activated: 5",
            agent, sendToBackoffice, default);

    [PostgresFact]
    public async Task Marked_form_starts_open_and_unmarked_form_never_lists()
    {
        var (ticket, agent, _) = await SeedAsync();
        var bo = new BackofficeFormRepository(_db.DataSource);
        var before = await bo.CountsAsync(Waiting, default);

        var marked = await FillAsync(ticket, agent, sendToBackoffice: true);
        var plain = await FillAsync(ticket, agent, sendToBackoffice: false);

        Assert.Equal(BackofficeFormStatus.Open, (await bo.GetItemAsync(marked, Waiting, default))!.Status);
        Assert.Null(await bo.GetItemAsync(plain, Waiting, default));
        Assert.Equal(before.Ready + 1, (await bo.CountsAsync(Waiting, default)).Ready);

        var log = (await bo.GetDetailAsync(marked, Waiting, default))!.Log;
        Assert.Equal(BackofficeFormAction.Submitted, Assert.Single(log).Action);
    }

    [PostgresFact]
    public async Task Two_concurrent_handles_have_exactly_one_winner()
    {
        var (ticket, agent, other) = await SeedAsync();
        var form = await FillAsync(ticket, agent, sendToBackoffice: true);
        var bo = new BackofficeFormRepository(_db.DataSource);

        var results = await Task.WhenAll(
            bo.HandleAsync(form, agent, "Contract CR-1001 updated", 1, default),
            bo.HandleAsync(form, other, "Contract CR-1001 updated", 1, default));

        Assert.Single(results, r => r is not null);
        var item = (await bo.GetItemAsync(form, Waiting, default))!;
        Assert.Equal(BackofficeFormStatus.Handled, item.Status);
        Assert.Contains(item.HandledByUserId!.Value, new[] { agent, other });
        Assert.Equal("Contract CR-1001 updated", item.HandledNote);
        Assert.Equal(1, item.HandledVersion);

        // A second handle on a handled form matches nothing.
        Assert.Null(await bo.HandleAsync(form, agent, "again", 1, default));
    }

    [PostgresFact]
    public async Task Edit_after_handling_puts_the_form_back_on_the_list()
    {
        var (ticket, agent, other) = await SeedAsync();
        var form = await FillAsync(ticket, agent, sendToBackoffice: true);
        var bo = new BackofficeFormRepository(_db.DataSource);
        Assert.NotNull(await bo.HandleAsync(form, other, "Checked", 1, default));

        var update = await new TicketFormRepository(_db.DataSource).UpdateAsync(
            form, 1, Answers("7"), "Licences activated: 7", agent, default);

        Assert.NotNull(update);
        Assert.Equal(2, update!.Version);
        Assert.Equal(BackofficeFormStatus.Handled, update.PreviousBoStatus);

        var detail = (await bo.GetDetailAsync(form, Waiting, default))!;
        Assert.Equal(BackofficeFormStatus.Open, detail.Item.Status);
        Assert.True(detail.Item.Reopened);
        Assert.Equal("5", detail.HandledAnswers![FieldId.ToString()]!.GetValue<string>());
        Assert.Equal("7", detail.Answers[FieldId.ToString()]!.GetValue<string>());
        Assert.Equal(BackofficeFormAction.Reopened, detail.Log[0].Action);

        // Handling again clears the "changed after handling" flag.
        Assert.NotNull(await bo.HandleAsync(form, other, "Rechecked", 2, default));
        Assert.False((await bo.GetItemAsync(form, Waiting, default))!.Reopened);
    }

    [PostgresFact]
    public async Task Handling_a_version_that_was_edited_meanwhile_is_refused()
    {
        var (ticket, agent, other) = await SeedAsync();
        var form = await FillAsync(ticket, agent, sendToBackoffice: true);
        var bo = new BackofficeFormRepository(_db.DataSource);
        // The technician saves v2 while the handler still looks at v1.
        Assert.NotNull(await new TicketFormRepository(_db.DataSource).UpdateAsync(
            form, 1, Answers("9"), "Licences activated: 9", agent, default));

        Assert.Null(await bo.HandleAsync(form, other, "Done", 1, default));
        Assert.Equal(BackofficeFormStatus.Open, (await bo.GetItemAsync(form, Waiting, default))!.Status);
        Assert.NotNull(await bo.HandleAsync(form, other, "Done", 2, default));
        Assert.Equal(2, (await bo.GetItemAsync(form, Waiting, default))!.HandledVersion);
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

        Assert.Null(await bo.GetItemAsync(removed, Waiting, default));
        Assert.Null(await bo.GetItemAsync(onDeleted, Waiting, default));
        var page = await bo.ListAsync(new BackofficeFormListQuery(null, null, null, null, null, 0, 200, Waiting), default);
        Assert.DoesNotContain(page.Items, i => i.Id == removed || i.Id == onDeleted);
        Assert.Null(await bo.HandleAsync(removed, agent, "x", 1, default));
    }

    [PostgresFact]
    public async Task List_filters_by_status_and_search_and_pages()
    {
        var (ticket, agent, other) = await SeedAsync();
        var bo = new BackofficeFormRepository(_db.DataSource);
        var tag = $"Sophos{Guid.NewGuid():N}"[..14];
        var a = await FillAsync(ticket, agent, true, tag);
        var b = await FillAsync(ticket, agent, true, tag);
        await bo.HandleAsync(b, other, "ok", 1, default);

        var open = await bo.ListAsync(new BackofficeFormListQuery(BackofficeFormStatus.Open, null, null, null, tag, 0, 50, Waiting), default);
        Assert.Equal(new[] { a }, open.Items.Select(i => i.Id));
        Assert.Equal(1, open.Total);

        var all = await bo.ListAsync(new BackofficeFormListQuery(null, null, null, agent, tag, 0, 1, Waiting), default);
        Assert.Single(all.Items);
        Assert.Equal(2, all.Total);

        await using var c = await _db.DataSource.OpenConnectionAsync();
        var number = await c.ExecuteScalarAsync<long>("SELECT number FROM tickets WHERE id = @ticket", new { ticket });
        var byNumber = await bo.ListAsync(new BackofficeFormListQuery(null, null, null, null, $"#{number}", 0, 50, Waiting), default);
        Assert.Contains(byNumber.Items, i => i.Id == a);
    }

    [PostgresFact]
    public async Task Pending_ticket_puts_its_open_form_under_waiting_and_out_of_the_badge()
    {
        var bo = new BackofficeFormRepository(_db.DataSource);
        var before = await bo.CountsAsync(Waiting, default);
        var (pendingTicket, agent, _) = await SeedAsync(stateCategory: "Pending");
        var (closedTicket, agent2, _) = await SeedAsync(stateCategory: "Closed");
        var tag = $"Veeam{Guid.NewGuid():N}"[..14];
        var waiting = await FillAsync(pendingTicket, agent, true, tag);
        var onClosed = await FillAsync(closedTicket, agent2, true, tag);

        Assert.True((await bo.GetItemAsync(waiting, Waiting, default))!.Waiting);
        // A form on a closed ticket is still work for back office.
        Assert.False((await bo.GetItemAsync(onClosed, Waiting, default))!.Waiting);

        var after = await bo.CountsAsync(Waiting, default);
        Assert.Equal(before.Ready + 1, after.Ready);
        Assert.Equal(before.Waiting + 1, after.Waiting);

        // Open tab: waiting for pickup first, then waiting for customer.
        var open = await bo.ListAsync(new BackofficeFormListQuery(BackofficeFormStatus.Open, null, null, null, tag, 0, 50, Waiting), default);
        Assert.Equal(new[] { onClosed, waiting }, open.Items.Select(i => i.Id));

        // The setting decides: with no waiting categories nothing waits.
        Assert.False((await bo.GetItemAsync(waiting, Array.Empty<string>(), default))!.Waiting);
    }

    [PostgresFact]
    public async Task Trigger_action_handles_only_open_forms_of_the_chosen_templates_on_that_ticket()
    {
        var bo = new BackofficeFormRepository(_db.DataSource);
        var (ticket, agent, planner) = await SeedAsync();
        var (otherTicket, agent2, _) = await SeedAsync();
        var request = await TemplateAsync();
        var unrelated = await TemplateAsync();

        var r1 = await FillAsync(ticket, agent, true, templateId: request);
        var r2 = await FillAsync(ticket, agent, true, templateId: request);
        var keep = await FillAsync(ticket, agent, true, templateId: unrelated);
        var elsewhere = await FillAsync(otherTicket, agent2, true, templateId: request);

        Assert.Equal(2, await bo.CountOpenOnTicketAsync(ticket, new[] { request }, default));
        var handled = await bo.HandleOpenOnTicketAsync(ticket, new[] { request }, planner, "Planned on 25/11 08:00", default);

        Assert.Equal(new[] { r1, r2 }.OrderBy(x => x), handled.OrderBy(x => x));
        foreach (var id in new[] { r1, r2 })
        {
            var d = (await bo.GetDetailAsync(id, Waiting, default))!;
            Assert.Equal(BackofficeFormStatus.Handled, d.Item.Status);
            Assert.Equal(planner, d.Item.HandledByUserId);
            Assert.Equal("Planned on 25/11 08:00", d.Item.HandledNote);
            Assert.Equal(BackofficeFormAction.Handled, d.Log[0].Action);
        }
        Assert.Equal(BackofficeFormStatus.Open, (await bo.GetItemAsync(keep, Waiting, default))!.Status);
        Assert.Equal(BackofficeFormStatus.Open, (await bo.GetItemAsync(elsewhere, Waiting, default))!.Status);

        // Nothing left: a second run is a no-op; a system run (no user) works too.
        Assert.Empty(await bo.HandleOpenOnTicketAsync(ticket, new[] { request }, planner, "again", default));
        Assert.Single(await bo.HandleOpenOnTicketAsync(otherTicket, new[] { request }, null, "", default));
    }
}
