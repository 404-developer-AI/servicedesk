using Microsoft.Extensions.Logging.Abstractions;
using Servicedesk.Domain.Tickets;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Persistence.Tickets;
using Servicedesk.Infrastructure.Settings;
using Servicedesk.Infrastructure.Tickets;
using Servicedesk.Infrastructure.Triggers;
using Servicedesk.Infrastructure.Triggers.StatusGate;
using Xunit;

namespace Servicedesk.Api.Tests;

/// v0.0.102 — pins the bulk-action contract: every ticket goes through the
/// shared mutation service's precheck, tickets that fail a rule (no access,
/// status outside queue scope, status gate) are skipped and never mutated,
/// one failing ticket does not stop the batch, the selection cap and the
/// "nothing to change" guard reject up front, and one batch-level audit row
/// is written with the tally.
public sealed class TicketBulkActionServiceTests
{
    private static readonly TicketMutationActor Actor = new(
        Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"), "Agent",
        "agent@desk.test", "Agent", "127.0.0.1", "xunit");

    private static readonly Guid StatusId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");

    [Fact]
    public async Task Skips_tickets_that_fail_a_rule_and_never_mutates_them()
    {
        var okId = Guid.NewGuid();
        var noAccessId = Guid.NewGuid();
        var scopeId = Guid.NewGuid();
        var gateId = Guid.NewGuid();
        var missingId = Guid.NewGuid();
        var checklistId = Guid.NewGuid();

        var mutations = new FakeMutations();
        mutations.Verdicts[okId] = new FieldUpdatePrecheck(TicketMutationCheck.Ok, Detail(okId, 1), Array.Empty<MatchedStatusGate>());
        mutations.Verdicts[noAccessId] = FieldUpdatePrecheck.Fail(TicketMutationCheck.NoAccess);
        mutations.Verdicts[scopeId] = FieldUpdatePrecheck.Fail(TicketMutationCheck.StatusNotInQueueScope);
        mutations.Verdicts[gateId] = new FieldUpdatePrecheck(TicketMutationCheck.Ok, Detail(gateId, 4), new[] { Gate() });
        mutations.Verdicts[missingId] = FieldUpdatePrecheck.Fail(TicketMutationCheck.NotFound);
        mutations.Verdicts[checklistId] = FieldUpdatePrecheck.Blocked(new[]
        {
            new Servicedesk.Infrastructure.Checklists.ChecklistBlocker(Guid.NewGuid(), "Onboarding", 3),
        });

        var audit = new RecordingAudit();
        var svc = Build(mutations, audit);

        var result = await svc.ExecuteAsync(Actor, Request(new[] { okId, noAccessId, scopeId, gateId, missingId, checklistId }, statusId: StatusId), default);

        Assert.Equal(6, result.Total);
        Assert.Equal(1, result.Succeeded);
        Assert.Equal(5, result.Skipped.Count);
        Assert.Equal(TicketBulkSkipReason.ChecklistIncomplete, result.Skipped.Single(s => s.TicketId == checklistId).Reason);
        Assert.Equal(TicketBulkSkipReason.NoAccess, result.Skipped.Single(s => s.TicketId == noAccessId).Reason);
        Assert.Equal(TicketBulkSkipReason.StatusNotInQueueScope, result.Skipped.Single(s => s.TicketId == scopeId).Reason);
        Assert.Equal(TicketBulkSkipReason.GateRequired, result.Skipped.Single(s => s.TicketId == gateId).Reason);
        Assert.Equal(4, result.Skipped.Single(s => s.TicketId == gateId).Number);
        Assert.Equal(TicketBulkSkipReason.NotFound, result.Skipped.Single(s => s.TicketId == missingId).Reason);

        // Only the passing ticket was written + published.
        Assert.Equal(new[] { okId }, mutations.Applied.Select(a => a.TicketId).ToArray());
        Assert.Equal(new[] { okId }, mutations.PublishedFieldUpdates.ToArray());
        Assert.Empty(mutations.AddedEvents);

        // Every leg carries the batch id so timeline + audit can correlate.
        Assert.All(mutations.Applied, a => Assert.Equal(result.BatchId, a.Update.BulkBatchId));

        var batchRow = Assert.Single(audit.Rows, r => r.EventType == "ticket.bulk_action");
        Assert.Equal(result.BatchId.ToString(), batchRow.Target);
    }

    [Fact]
    public async Task Message_is_posted_before_field_changes_and_carries_the_batch_id()
    {
        var id = Guid.NewGuid();
        var mutations = new FakeMutations();
        mutations.Verdicts[id] = new FieldUpdatePrecheck(TicketMutationCheck.Ok, Detail(id, 7), Array.Empty<MatchedStatusGate>());
        var svc = Build(mutations, new RecordingAudit());

        var result = await svc.ExecuteAsync(Actor,
            Request(new[] { id }, statusId: StatusId, message: "<p>Closing in bulk</p>", isInternal: false), default);

        Assert.Equal(1, result.Succeeded);
        var evt = Assert.Single(mutations.AddedEvents);
        Assert.Equal("Comment", evt.Input.EventType);
        Assert.False(evt.Input.IsInternal);
        Assert.Contains(result.BatchId.ToString(), evt.Input.MetadataJson);
        Assert.Equal(new[] { "event", "fields" }, mutations.Order.ToArray());
    }

    [Fact]
    public async Task One_failing_ticket_does_not_stop_the_batch()
    {
        var boom = Guid.NewGuid();
        var fine = Guid.NewGuid();
        var mutations = new FakeMutations();
        mutations.Verdicts[boom] = new FieldUpdatePrecheck(TicketMutationCheck.Ok, Detail(boom, 1), Array.Empty<MatchedStatusGate>());
        mutations.Verdicts[fine] = new FieldUpdatePrecheck(TicketMutationCheck.Ok, Detail(fine, 2), Array.Empty<MatchedStatusGate>());
        mutations.ThrowOnApply.Add(boom);
        var svc = Build(mutations, new RecordingAudit());

        var result = await svc.ExecuteAsync(Actor, Request(new[] { boom, fine }, statusId: StatusId), default);

        Assert.Equal(1, result.Succeeded);
        var skipped = Assert.Single(result.Skipped);
        Assert.Equal(boom, skipped.TicketId);
        Assert.Equal(TicketBulkSkipReason.Failed, skipped.Reason);
        Assert.Contains(fine, mutations.PublishedFieldUpdates);
    }

    [Fact]
    public async Task Rejects_when_over_the_configured_cap_without_touching_anything()
    {
        var mutations = new FakeMutations();
        var svc = Build(mutations, new RecordingAudit(), maxSelection: 2);
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };

        var ex = await Assert.ThrowsAsync<TicketBulkActionRejectedException>(
            () => svc.ExecuteAsync(Actor, Request(ids, statusId: StatusId), default));

        Assert.Equal("too_many", ex.Code);
        Assert.Empty(mutations.Prechecked);
    }

    [Fact]
    public async Task Rejects_a_request_that_changes_nothing()
    {
        var mutations = new FakeMutations();
        var svc = Build(mutations, new RecordingAudit());

        var ex = await Assert.ThrowsAsync<TicketBulkActionRejectedException>(
            () => svc.ExecuteAsync(Actor, Request(new[] { Guid.NewGuid() }), default));

        Assert.Equal("nothing_to_change", ex.Code);
        Assert.Empty(mutations.Prechecked);
    }

    [Fact]
    public async Task Rejects_when_the_feature_is_disabled()
    {
        var mutations = new FakeMutations();
        var svc = Build(mutations, new RecordingAudit(), enabled: false);

        var ex = await Assert.ThrowsAsync<TicketBulkActionRejectedException>(
            () => svc.ExecuteAsync(Actor, Request(new[] { Guid.NewGuid() }, statusId: StatusId), default));

        Assert.Equal("bulk_disabled", ex.Code);
    }

    [Fact]
    public async Task Duplicate_ids_are_collapsed_so_a_note_is_never_posted_twice()
    {
        var id = Guid.NewGuid();
        var mutations = new FakeMutations();
        mutations.Verdicts[id] = new FieldUpdatePrecheck(TicketMutationCheck.Ok, Detail(id, 1), Array.Empty<MatchedStatusGate>());
        var svc = Build(mutations, new RecordingAudit());

        var result = await svc.ExecuteAsync(Actor, Request(new[] { id, id, id }, message: "<p>x</p>"), default);

        Assert.Equal(1, result.Total);
        Assert.Single(mutations.AddedEvents);
    }

    // ---- helpers ----

    private static TicketBulkActionService Build(FakeMutations mutations, IAuditLogger audit, bool enabled = true, int maxSelection = 100)
        => new(mutations, new StubSettings(enabled, maxSelection), audit, NullLogger<TicketBulkActionService>.Instance);

    private static TicketBulkActionRequest Request(
        IReadOnlyList<Guid> ids, Guid? statusId = null, string? message = null, bool isInternal = true, DateTime? pendingTillUtc = null)
        => new(ids, message, isInternal, statusId, null, null, null, false, pendingTillUtc);

    [Fact]
    public async Task Pending_till_travels_with_a_status_change_and_is_dropped_without_one()
    {
        var withStatus = Guid.NewGuid();
        var mutations = new FakeMutations();
        mutations.Verdicts[withStatus] = new FieldUpdatePrecheck(TicketMutationCheck.Ok, Detail(withStatus, 1), Array.Empty<MatchedStatusGate>());
        var svc = Build(mutations, new RecordingAudit());
        var till = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);

        await svc.ExecuteAsync(Actor, Request(new[] { withStatus }, statusId: StatusId, pendingTillUtc: till), default);
        Assert.Equal(till, Assert.Single(mutations.Applied).Update.PendingTillUtc);

        // Without a status the value is meaningless for a bulk edit: it is
        // dropped and, with nothing else, the request is rejected as empty.
        var ex = await Assert.ThrowsAsync<TicketBulkActionRejectedException>(() =>
            svc.ExecuteAsync(Actor, Request(new[] { withStatus }, pendingTillUtc: till), default));
        Assert.Equal("nothing_to_change", ex.Code);
    }

    private static TicketDetail Detail(Guid id, long number)
    {
        var t = new Ticket(
            id, number, "Subject", Guid.NewGuid(), null, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null,
            "web", null, DateTime.UtcNow, DateTime.UtcNow, null, null, null, null, false);
        return new TicketDetail(t, new TicketBody(id, "body", null), Array.Empty<TicketEvent>(), Array.Empty<TicketEventPin>());
    }

    private static MatchedStatusGate Gate() => new(
        Guid.NewGuid(), "Confirm close", "prompt_confirm", "Sure?", null,
        Array.Empty<GateQuestion>(), "Yes", "No", "internal", "", StatusId, null, null, null);

    private sealed class FakeMutations : ITicketMutationService
    {
        public readonly Dictionary<Guid, FieldUpdatePrecheck> Verdicts = new();
        public readonly HashSet<Guid> ThrowOnApply = new();
        public readonly List<Guid> Prechecked = new();
        public readonly List<(Guid TicketId, TicketFieldUpdate Update)> Applied = new();
        public readonly List<Guid> PublishedFieldUpdates = new();
        public readonly List<(Guid TicketId, NewTicketEvent Input)> AddedEvents = new();
        public readonly List<string> Order = new();
        private long _nextEventId = 1;

        public Task<AccessPrecheck> PrecheckAccessAsync(TicketMutationActor actor, Guid ticketId, CancellationToken ct)
        {
            var v = Verdicts.TryGetValue(ticketId, out var p) ? p : FieldUpdatePrecheck.Fail(TicketMutationCheck.NotFound);
            return Task.FromResult(new AccessPrecheck(v.Check, v.Ticket));
        }

        public Task<FieldUpdatePrecheck> PrecheckFieldUpdateAsync(TicketMutationActor actor, Guid ticketId, TicketFieldUpdate update, CancellationToken ct)
        {
            Prechecked.Add(ticketId);
            return Task.FromResult(Verdicts.TryGetValue(ticketId, out var p) ? p : FieldUpdatePrecheck.Fail(TicketMutationCheck.NotFound));
        }

        public Task<TicketDetail?> ApplyFieldUpdateAsync(Guid ticketId, TicketFieldUpdate update, Guid actorUserId, CancellationToken ct)
        {
            if (ThrowOnApply.Contains(ticketId)) throw new InvalidOperationException("boom");
            Applied.Add((ticketId, update));
            Order.Add("fields");
            return Task.FromResult(Verdicts[ticketId].Ticket);
        }

        public Task PublishFieldUpdateAsync(TicketMutationActor actor, Guid ticketId, TicketFieldUpdate update, object auditPayload, TriggerChangeSet? changeSet, CancellationToken ct)
        {
            PublishedFieldUpdates.Add(ticketId);
            return Task.CompletedTask;
        }

        public Task<TicketEvent?> AddEventAsync(Guid ticketId, NewTicketEvent input, CancellationToken ct)
        {
            AddedEvents.Add((ticketId, input));
            Order.Add("event");
            var evt = new TicketEvent(_nextEventId++, ticketId, input.EventType, input.AuthorUserId, null, null,
                input.BodyText, input.BodyHtml, input.MetadataJson ?? "{}", input.IsInternal, DateTime.UtcNow, null, null);
            return Task.FromResult<TicketEvent?>(evt);
        }

        public Task PublishEventAsync(TicketMutationActor actor, Guid ticketId, TicketEvent evt, object auditPayload, CancellationToken ct)
            => Task.CompletedTask;
    }

    private sealed class RecordingAudit : IAuditLogger
    {
        public readonly List<AuditEvent> Rows = new();
        public Task LogAsync(AuditEvent evt, CancellationToken cancellationToken = default)
        {
            Rows.Add(evt);
            return Task.CompletedTask;
        }
    }

    private sealed class StubSettings : ISettingsService
    {
        private readonly bool _enabled;
        private readonly int _max;
        public StubSettings(bool enabled, int max) { _enabled = enabled; _max = max; }
        public Task EnsureDefaultsAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<T> GetAsync<T>(string key, CancellationToken ct = default)
        {
            object val = key switch
            {
                SettingKeys.Tickets.BulkActionsEnabled => _enabled,
                SettingKeys.Tickets.BulkActionsMaxSelection => _max,
                _ => default(T)!,
            };
            return Task.FromResult((T)val);
        }
        public Task SetAsync<T>(string key, T value, string actor, string actorRole, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<SettingEntry>> ListAsync(string? category = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SettingEntry>>(Array.Empty<SettingEntry>());
    }
}
