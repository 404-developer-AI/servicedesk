using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Servicedesk.Api.Auth;
using Servicedesk.Api.Tests.TestInfrastructure;
using Servicedesk.Domain.Tickets;
using Servicedesk.Infrastructure.Access;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Checklists;
using Servicedesk.Infrastructure.Integrations.Zammad;
using Servicedesk.Infrastructure.Persistence.Taxonomy;
using Servicedesk.Infrastructure.Persistence.Tickets;
using Servicedesk.Infrastructure.Realtime;
using Servicedesk.Infrastructure.Settings;
using Servicedesk.Infrastructure.Sla;
using Servicedesk.Infrastructure.Tickets;
using Servicedesk.Infrastructure.Triggers;
using Servicedesk.Infrastructure.Triggers.Actions;
using Servicedesk.Infrastructure.Triggers.StatusGate;
using Xunit;

namespace Servicedesk.Api.Tests;

/// v0.1.44 — a pending-till in the past is refused on every writer, on the
/// server clock. Root cause (Ticket#19964): the scheduler treats a reminder
/// boundary as fired once a run exists at or after it, so a pending-till set
/// before the last run on a ticket was never picked up again and the ticket
/// stayed in Pending forever. Writers: side-panel PATCH + bulk (mutation
/// precheck), ticket create (endpoint), trigger set_pending_till (failed
/// action), Zammad import (elapsed value → empty). The bulk leg lives in
/// <see cref="TicketBulkActionServiceTests"/>.
public sealed class PendingTillInPastTests
{
    private static readonly DateTime Now = new(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid TicketId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid QueueId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    // ---- the rule ----------------------------------------------------------

    [Fact]
    public void Rule_treats_now_and_earlier_as_past_and_null_as_never_past()
    {
        Assert.True(PendingTillRules.IsInPast(Now, Now));
        Assert.True(PendingTillRules.IsInPast(Now.AddMinutes(-1), Now));
        Assert.False(PendingTillRules.IsInPast(Now.AddMinutes(1), Now));
        Assert.False(PendingTillRules.IsInPast(null, Now));
    }

    [Fact]
    public void Rule_compares_instants_regardless_of_kind()
    {
        var unspecified = DateTime.SpecifyKind(Now.AddMinutes(-1), DateTimeKind.Unspecified);
        Assert.True(PendingTillRules.IsInPast(unspecified, Now));
        Assert.True(PendingTillRules.SameInstant(DateTime.SpecifyKind(Now, DateTimeKind.Unspecified), Now));
        Assert.False(PendingTillRules.SameInstant(null, Now));
        Assert.True(PendingTillRules.SameInstant(null, null));
    }

    // ---- side panel + bulk: shared mutation precheck -------------------------

    [Fact]
    public async Task Precheck_refuses_a_past_pending_till()
    {
        var svc = MutationService(Ticket(pendingTill: Now.AddDays(1)));
        var pre = await svc.PrecheckFieldUpdateAsync(Actor, TicketId,
            new TicketFieldUpdate(PendingTillUtc: Now.AddMinutes(-5)), default);
        Assert.Equal(TicketMutationCheck.PendingTillInPast, pre.Check);
    }

    [Fact]
    public async Task Precheck_accepts_a_future_pending_till()
    {
        var svc = MutationService(Ticket(pendingTill: null));
        var pre = await svc.PrecheckFieldUpdateAsync(Actor, TicketId,
            new TicketFieldUpdate(PendingTillUtc: Now.AddHours(2)), default);
        Assert.Equal(TicketMutationCheck.Ok, pre.Check);
    }

    [Fact]
    public async Task Precheck_lets_the_unchanged_current_value_through()
    {
        // A stale tab re-sending the value the ticket already holds is not a
        // change, so it must not block the rest of the edit.
        var stored = Now.AddDays(-3);
        var svc = MutationService(Ticket(pendingTill: stored));
        var pre = await svc.PrecheckFieldUpdateAsync(Actor, TicketId,
            new TicketFieldUpdate(PendingTillUtc: DateTime.SpecifyKind(stored, DateTimeKind.Unspecified)), default);
        Assert.Equal(TicketMutationCheck.Ok, pre.Check);
    }

    [Fact]
    public async Task Precheck_still_hides_a_ticket_the_actor_cannot_access()
    {
        // Access is checked first: a past value never reveals that the
        // ticket exists.
        var svc = MutationService(Ticket(pendingTill: null), hasQueueAccess: false);
        var pre = await svc.PrecheckFieldUpdateAsync(Actor, TicketId,
            new TicketFieldUpdate(PendingTillUtc: Now.AddDays(-1)), default);
        Assert.Equal(TicketMutationCheck.NoAccess, pre.Check);
    }

    // ---- endpoints -----------------------------------------------------------

    [Fact]
    public async Task Patch_with_a_past_pending_till_is_a_400()
    {
        using var factory = new SecurityBaselineFactory();
        using var host = factory.WithWebHostBuilder(b => b.ConfigureServices(services =>
        {
            services.RemoveAll<ITicketRepository>();
            services.AddSingleton(Fake<ITicketRepository>.Create(TicketRepoHandler(Ticket(pendingTill: null))));
            services.RemoveAll<IQueueAccessService>();
            services.AddSingleton(Fake<IQueueAccessService>.Create(QueueAccessHandler(true)));
            ReplaceDbBacked(services);
        }));
        var client = await AgentClientAsync(factory, host.CreateClient);

        var response = await client.PatchAsJsonAsync($"/api/tickets/{TicketId}",
            new { pendingTillUtc = DateTime.UtcNow.AddHours(-1) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(PendingTillRules.PastCode, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Create_with_a_past_pending_till_is_a_400_before_anything_is_written()
    {
        using var factory = new SecurityBaselineFactory();
        var tickets = new List<string>();
        using var host = factory.WithWebHostBuilder(b => b.ConfigureServices(services =>
        {
            // Any repository call would mean the check ran too late.
            services.RemoveAll<ITicketRepository>();
            services.AddSingleton(Fake<ITicketRepository>.Create((m, _) => { tickets.Add(m.Name); throw new InvalidOperationException(m.Name); }));
            ReplaceDbBacked(services);
        }));
        var client = await AgentClientAsync(factory, host.CreateClient);

        var response = await client.PostAsJsonAsync("/api/tickets", new
        {
            subject = "Printer offline",
            requesterContactId = Guid.NewGuid(),
            queueId = QueueId,
            statusId = Guid.NewGuid(),
            priorityId = Guid.NewGuid(),
            pendingTillUtc = DateTime.UtcNow.AddMinutes(-10),
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(PendingTillRules.PastCode, await response.Content.ReadAsStringAsync());
        Assert.Empty(tickets);
    }

    // ---- trigger set_pending_till -------------------------------------------

    [Fact]
    public async Task Trigger_with_a_past_absolute_fails_without_writing()
    {
        // Null-backed mutator: reaching it would throw.
        var handler = new SetPendingTillHandler(new SystemFieldMutator(null!, null!), null!);
        var action = JsonDocument.Parse("""{ "kind":"set_pending_till", "absolute":"2026-10-13T07:00:00Z" }""").RootElement;

        var result = await handler.ApplyAsync(action, TriggerContext(), default);

        Assert.Equal(TriggerActionStatus.Failed, result.Status);
        Assert.Contains("in the past", result.FailureReason);
    }

    [Fact]
    public void Trigger_validator_refuses_a_negative_relative_duration()
    {
        const string actions = """[{"kind":"set_pending_till","relative":"-PT1H"}]""";
        var r = TriggerValidator.Validate("t", "action", "selective", "{\"op\":\"AND\",\"items\":[]}", actions, null, null);
        Assert.False(r.IsValid);
        Assert.Contains("positive", r.Error!);
    }

    // ---- Zammad import -------------------------------------------------------

    [Fact]
    public void Zammad_import_drops_an_elapsed_pending_time_and_keeps_a_future_one()
    {
        Assert.Null(ZammadImportWriter.NormalizePendingTill(Now.AddDays(-2), Now));
        Assert.Null(ZammadImportWriter.NormalizePendingTill(null, Now));

        var future = DateTime.SpecifyKind(Now.AddDays(2), DateTimeKind.Unspecified);
        var kept = ZammadImportWriter.NormalizePendingTill(future, Now);
        Assert.Equal(DateTimeKind.Utc, kept!.Value.Kind);
        Assert.Equal(Now.AddDays(2), kept.Value);
    }

    // ---- plumbing ------------------------------------------------------------

    private static readonly TicketMutationActor Actor = new(
        Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002"), "Agent", "agent@desk.test", "Agent", "127.0.0.1", "xunit");

    private static Ticket Ticket(DateTime? pendingTill) => new(
        TicketId, 19964, "Subject", Guid.NewGuid(), null, QueueId, Guid.NewGuid(), Guid.NewGuid(), null,
        "web", null, Now, Now, null, null, null, null, false, PendingTillUtc: pendingTill);

    private static TicketMutationService MutationService(Ticket ticket, bool hasQueueAccess = true)
        => new(
            Fake<ITicketRepository>.Create(TicketRepoHandler(ticket)),
            Fake<IQueueAccessService>.Create(QueueAccessHandler(hasQueueAccess)),
            Fake<ITaxonomyRepository>.Create(Unexpected),
            Fake<IStatusGateService>.Create(Unexpected),
            Fake<IAuditLogger>.Create(Unexpected),
            Fake<ITicketListNotifier>.Create(Unexpected),
            Fake<ISlaEngine>.Create(Unexpected),
            Fake<ITriggerService>.Create(Unexpected),
            Fake<IChecklistCloseGuard>.Create(Unexpected),
            Fake<ISettingsService>.Create(Unexpected),
            new FixedClock(Now));

    private static Func<MethodInfo, object?[]?, object?> TicketRepoHandler(Ticket ticket) => (m, args) => m.Name switch
    {
        "GetCoreAsync" => Task.FromResult<Ticket?>((Guid)args![0]! == ticket.Id ? ticket : null),
        "GetByIdAsync" => Task.FromResult<TicketDetail?>((Guid)args![0]! == ticket.Id
            ? new TicketDetail(ticket, new TicketBody(ticket.Id, "body", null), Array.Empty<TicketEvent>(), Array.Empty<TicketEventPin>())
            : null),
        _ => Unexpected(m, args),
    };

    private static Func<MethodInfo, object?[]?, object?> QueueAccessHandler(bool allow) => (m, args) => m.Name switch
    {
        "HasQueueAccessAsync" => Task.FromResult(allow),
        _ => Unexpected(m, args),
    };

    private static object? Unexpected(MethodInfo m, object?[]? _) =>
        throw new InvalidOperationException($"Unexpected call: {m.DeclaringType?.Name}.{m.Name}");

    private static TriggerEvaluationContext TriggerContext()
    {
        var ticket = Ticket(pendingTill: null);
        return new TriggerEvaluationContext(ticket.Id, ticket, null, TriggerChangeSet.ArticleOnly(isTicketCreation: false), DateTime.UtcNow, Guid.NewGuid());
    }

    /// The endpoints resolve these (Npgsql-backed in production) as handler
    /// parameters; none of them may be reached before the pending-till check.
    private static void ReplaceDbBacked(IServiceCollection services)
    {
        foreach (var type in new[]
        {
            typeof(Servicedesk.Infrastructure.Persistence.Companies.ICompanyRepository),
            typeof(Servicedesk.Infrastructure.Mail.Ingest.IContactLookupService),
            typeof(ITaxonomyRepository), typeof(IStatusGateService), typeof(ISlaEngine),
            typeof(ITriggerService), typeof(IChecklistCloseGuard), typeof(IQueueAccessService),
        })
        {
            if (services.Any(d => d.ServiceType == type && d.ImplementationInstance is not null)) continue;
            services.RemoveAll(type);
            var create = typeof(Fake<>).MakeGenericType(type).GetMethod(nameof(Fake<object>.Create))!;
            services.AddSingleton(type, create.Invoke(null, new object[] { (Func<MethodInfo, object?[]?, object?>)Unexpected })!);
        }
    }

    private static async Task<HttpClient> AgentClientAsync(SecurityBaselineFactory factory, Func<HttpClient> createClient)
    {
        var userId = Guid.NewGuid();
        factory.Sessions.Roles[userId] = "Agent";
        var sessionId = await factory.Sessions.CreateAsync(
            userId, ip: null, userAgent: null, lifetime: TimeSpan.FromHours(1), amr: "pwd");
        var cookieName = await factory.Settings.GetAsync<string>(SettingKeys.Security.SessionCookieName);
        var csrf = DoubleSubmitCsrfMiddleware.GenerateToken();
        var client = createClient();
        client.DefaultRequestHeaders.Add(
            "Cookie", $"{cookieName}={sessionId}; {DoubleSubmitCsrfMiddleware.CookieName}={csrf}");
        client.DefaultRequestHeaders.Add(DoubleSubmitCsrfMiddleware.HeaderName, csrf);
        return client;
    }

    private sealed class FixedClock(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now, TimeSpan.Zero);
    }

    /// Interface fake that routes every call (including default interface
    /// methods) through one handler.
    public class Fake<T> : DispatchProxy where T : class
    {
        private Func<MethodInfo, object?[]?, object?> _handler = null!;

        public static T Create(Func<MethodInfo, object?[]?, object?> handler)
        {
            var proxy = Create<T, Fake<T>>();
            ((Fake<T>)(object)proxy)._handler = handler;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => _handler(targetMethod!, args);
    }
}
