using System.Text.Json;
using Dapper;
using Servicedesk.Api.Tests.TestInfrastructure;
using Servicedesk.Infrastructure.Auth;
using Servicedesk.Infrastructure.Persistence.Taxonomy;
using Servicedesk.Infrastructure.Persistence.Tickets;
using Servicedesk.Infrastructure.Settings;
using Servicedesk.Infrastructure.Sla;
using Servicedesk.Infrastructure.Triggers;
using Servicedesk.Infrastructure.Triggers.Actions;
using Servicedesk.Infrastructure.Triggers.Templating;
using Xunit;

namespace Servicedesk.Api.Tests.Database;

/// The trigger field actions against real SQL: Set queue, Set priority,
/// Set owner and Add internal note. They pin behaviour (the ticket row, the
/// timeline event, the result) rather than the write path, so they keep
/// holding when the handlers move onto TicketMutationService (ROADMAP §2
/// step 6, C3).
[Collection(PostgresCollection.Name)]
public sealed class TriggerFieldHandlersDbTests
{
    private readonly PostgresDatabase _db;
    private readonly List<string> _slaCalls = new();

    public TriggerFieldHandlersDbTests(PostgresDatabase db) => _db = db;

    // ---- set_queue ----------------------------------------------------------

    [PostgresFact]
    public async Task Set_queue_moves_the_ticket_and_writes_a_system_queue_change()
    {
        var (seed, ticketId, _) = await TicketAsync();
        var to = await seed.QueueAsync("Second line");

        var result = await RunAsync(SetQueue(), ticketId, $$"""{ "queue_id": "{{to}}" }""");

        Assert.Equal(TriggerActionStatus.Applied, result.Status);
        Assert.Equal(to, await ColumnAsync<Guid>(ticketId, "queue_id"));
        var evt = await SingleEventAsync(ticketId, "QueueChange");
        Assert.Null(evt.AuthorUserId);
        Assert.False(evt.IsInternal);
        Assert.Contains(TriggerIdText, evt.Metadata);
        Assert.Contains(nameof(ISlaEngine.OnTicketFieldsChangedAsync), _slaCalls);
    }

    [PostgresFact]
    public async Task Set_queue_to_the_current_queue_is_a_no_op_without_an_event()
    {
        var (_, ticketId, queue) = await TicketAsync();

        var result = await RunAsync(SetQueue(), ticketId, $$"""{ "queue_id": "{{queue}}" }""");

        Assert.Equal(TriggerActionStatus.NoOp, result.Status);
        Assert.Equal(0, await EventCountAsync(ticketId));
    }

    [PostgresFact]
    public async Task Set_queue_never_moves_a_project_ticket_out_of_the_pinned_project_queue()
    {
        var seed = new DbSeed(_db.DataSource);
        var (pinned, status, priority) = await seed.TaxonomyAsync();
        var elsewhere = await seed.QueueAsync("Elsewhere");
        var project = await seed.TicketAsync("Project", await seed.ContactAsync(), pinned, status, priority, isProject: true);
        var settings = new InMemorySettingsService().With(SettingKeys.Projects.QueueId, pinned.ToString());

        var result = await RunAsync(SetQueue(settings), project, $$"""{ "queue_id": "{{elsewhere}}" }""");

        Assert.Equal(TriggerActionStatus.NoOp, result.Status);
        Assert.Equal(pinned, await ColumnAsync<Guid>(project, "queue_id"));
        Assert.Equal(0, await EventCountAsync(project));
    }

    [PostgresFact]
    public async Task Set_queue_to_an_unknown_queue_fails_and_changes_nothing()
    {
        var (_, ticketId, queue) = await TicketAsync();

        var result = await RunAsync(SetQueue(), ticketId, $$"""{ "queue_id": "{{Guid.NewGuid()}}" }""");

        Assert.Equal(TriggerActionStatus.Failed, result.Status);
        Assert.Equal(queue, await ColumnAsync<Guid>(ticketId, "queue_id"));
        Assert.Equal(0, await EventCountAsync(ticketId));
    }

    [PostgresFact]
    public async Task Set_queue_without_a_queue_id_fails()
    {
        var (_, ticketId, _) = await TicketAsync();

        var result = await RunAsync(SetQueue(), ticketId, "{}");

        Assert.Equal(TriggerActionStatus.Failed, result.Status);
    }

    // ---- set_priority -------------------------------------------------------

    [PostgresFact]
    public async Task Set_priority_changes_the_priority_and_writes_a_priority_change()
    {
        var (seed, ticketId, _) = await TicketAsync();
        var urgent = await seed.PriorityAsync();

        var result = await RunAsync(new SetPriorityHandler(Mutator()), ticketId, $$"""{ "priority_id": "{{urgent}}" }""");

        Assert.Equal(TriggerActionStatus.Applied, result.Status);
        Assert.Equal(urgent, await ColumnAsync<Guid>(ticketId, "priority_id"));
        Assert.Contains(TriggerIdText, (await SingleEventAsync(ticketId, "PriorityChange")).Metadata);
    }

    [PostgresFact]
    public async Task Set_priority_to_the_current_priority_is_a_no_op()
    {
        var (_, ticketId, _) = await TicketAsync();
        var current = await ColumnAsync<Guid>(ticketId, "priority_id");

        var result = await RunAsync(new SetPriorityHandler(Mutator()), ticketId, $$"""{ "priority_id": "{{current}}" }""");

        Assert.Equal(TriggerActionStatus.NoOp, result.Status);
        Assert.Equal(0, await EventCountAsync(ticketId));
    }

    [PostgresFact]
    public async Task Set_priority_without_a_priority_id_fails()
    {
        var (_, ticketId, _) = await TicketAsync();

        var result = await RunAsync(new SetPriorityHandler(Mutator()), ticketId, """{ "priority_id": "not-a-guid" }""");

        Assert.Equal(TriggerActionStatus.Failed, result.Status);
    }

    // ---- set_owner ----------------------------------------------------------

    [PostgresFact]
    public async Task Set_owner_assigns_the_agent_and_writes_an_assignment_change()
    {
        var (seed, ticketId, _) = await TicketAsync();
        var agent = await seed.UserAsync();

        var result = await RunAsync(SetOwner(), ticketId, $$"""{ "user_id": "{{agent}}" }""");

        Assert.Equal(TriggerActionStatus.Applied, result.Status);
        Assert.Equal(agent, await ColumnAsync<Guid?>(ticketId, "assignee_user_id"));
        Assert.Contains(TriggerIdText, (await SingleEventAsync(ticketId, "AssignmentChange")).Metadata);
    }

    [PostgresFact]
    public async Task Set_owner_with_a_null_user_clears_the_assignee()
    {
        var (seed, ticketId, _) = await TicketAsync();
        await SetColumnAsync(ticketId, "assignee_user_id", await seed.UserAsync());

        var result = await RunAsync(SetOwner(), ticketId, """{ "user_id": null }""");

        Assert.Equal(TriggerActionStatus.Applied, result.Status);
        Assert.Null(await ColumnAsync<Guid?>(ticketId, "assignee_user_id"));
    }

    [PostgresFact]
    public async Task Set_owner_to_an_unknown_user_fails_and_keeps_the_assignee()
    {
        var (_, ticketId, _) = await TicketAsync();

        var result = await RunAsync(SetOwner(), ticketId, $$"""{ "user_id": "{{Guid.NewGuid()}}" }""");

        Assert.Equal(TriggerActionStatus.Failed, result.Status);
        Assert.Null(await ColumnAsync<Guid?>(ticketId, "assignee_user_id"));
        Assert.Equal(0, await EventCountAsync(ticketId));
    }

    // ---- add_internal_note --------------------------------------------------

    [PostgresFact]
    public async Task Add_internal_note_writes_an_internal_system_note_and_recalcs_sla()
    {
        var (_, ticketId, _) = await TicketAsync();

        var result = await RunAsync(AddInternalNote(), ticketId,
            """{ "body_html": "<p>Escalated by rule</p>", "body_text": "Escalated by rule" }""");

        Assert.Equal(TriggerActionStatus.Applied, result.Status);
        var note = await SingleEventAsync(ticketId, "Note");
        Assert.True(note.IsInternal);
        Assert.Null(note.AuthorUserId);
        Assert.Equal("<p>Escalated by rule</p>", note.BodyHtml);
        Assert.Contains(TriggerIdText, note.Metadata);
        Assert.Contains(nameof(ISlaEngine.OnTicketEventAsync), _slaCalls);
    }

    [PostgresFact]
    public async Task Add_internal_note_without_a_body_fails_and_writes_nothing()
    {
        var (_, ticketId, _) = await TicketAsync();

        var result = await RunAsync(AddInternalNote(), ticketId, "{}");

        Assert.Equal(TriggerActionStatus.Failed, result.Status);
        Assert.Equal(0, await EventCountAsync(ticketId));
    }

    // ---- plumbing -----------------------------------------------------------

    private static readonly Guid TriggerId = Guid.Parse("7a000000-0000-0000-0000-000000000001");
    private static readonly string TriggerIdText = TriggerId.ToString();

    private SystemFieldMutator Mutator() => new(_db.DataSource, Sla());

    private ISlaEngine Sla() => Fake<ISlaEngine>.Create((method, _) =>
    {
        _slaCalls.Add(method.Name);
        return method.ReturnType == typeof(Task)
            ? Task.CompletedTask
            : throw new NotImplementedException($"Unexpected call: {method.Name}");
    });

    private SetQueueHandler SetQueue(InMemorySettingsService? settings = null) => new(
        Mutator(), settings ?? new InMemorySettingsService(),
        new TaxonomyRepository(_db.DataSource), new TicketRepository(_db.DataSource));

    // The fixed-value path never consults users or queue access (that is the
    // form-field path); any call there would be a behaviour change.
    private SetOwnerHandler SetOwner() => new(
        Mutator(),
        Fake<IUserService>.Create((m, _) => throw new InvalidOperationException("Unexpected: " + m.Name)),
        FakeQueueAccess.None(),
        new TicketRepository(_db.DataSource));

    // No render context in these runs, so the renderer must never be asked.
    private AddInternalNoteHandler AddInternalNote() => new(
        new TicketRepository(_db.DataSource),
        Fake<ITriggerTemplateRenderer>.Create((m, _) => throw new InvalidOperationException("Unexpected: " + m.Name)),
        Sla());

    private async Task<(DbSeed Seed, Guid TicketId, Guid QueueId)> TicketAsync()
    {
        var seed = new DbSeed(_db.DataSource);
        var (queue, status, priority) = await seed.TaxonomyAsync();
        var ticketId = await seed.TicketAsync("Trigger target", await seed.ContactAsync(), queue, status, priority);
        return (seed, ticketId, queue);
    }

    private async Task<TriggerActionResult> RunAsync(ITriggerActionHandler handler, Guid ticketId, string actionJson)
    {
        var ticket = (await new TicketRepository(_db.DataSource).GetCoreAsync(ticketId, default))!;
        var ctx = new TriggerEvaluationContext(ticketId, ticket, null, TriggerChangeSet.Empty, DateTime.UtcNow, TriggerId);
        using var action = JsonDocument.Parse(actionJson);
        return await handler.ApplyAsync(action.RootElement, ctx, default);
    }

    private async Task<T> ColumnAsync<T>(Guid ticketId, string column)
    {
        // Column names come from this file only — never from input.
        await using var c = await _db.DataSource.OpenConnectionAsync();
        return await c.QuerySingleAsync<T>($"SELECT {column} FROM tickets WHERE id = @ticketId", new { ticketId });
    }

    private async Task SetColumnAsync(Guid ticketId, string column, Guid value)
    {
        await using var c = await _db.DataSource.OpenConnectionAsync();
        await c.ExecuteAsync($"UPDATE tickets SET {column} = @value WHERE id = @ticketId", new { ticketId, value });
    }

    private async Task<int> EventCountAsync(Guid ticketId)
    {
        await using var c = await _db.DataSource.OpenConnectionAsync();
        return await c.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM ticket_events WHERE ticket_id = @ticketId", new { ticketId });
    }

    private async Task<EventRow> SingleEventAsync(Guid ticketId, string eventType)
    {
        await using var c = await _db.DataSource.OpenConnectionAsync();
        var rows = (await c.QueryAsync<EventRow>("""
            SELECT author_user_id AS AuthorUserId, is_internal AS IsInternal,
                   body_html AS BodyHtml, metadata::text AS Metadata
            FROM ticket_events WHERE ticket_id = @ticketId AND event_type = @eventType
            """, new { ticketId, eventType })).ToList();
        return Assert.Single(rows);
    }

    private sealed record EventRow(Guid? AuthorUserId, bool IsInternal, string? BodyHtml, string Metadata);
}
