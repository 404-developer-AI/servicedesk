using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Servicedesk.Api.Auth;
using Servicedesk.Api.Tests.TestInfrastructure;
using Servicedesk.Domain.Tickets;
using Servicedesk.Infrastructure.Access;
using Servicedesk.Infrastructure.Persistence.Tickets;
using Servicedesk.Infrastructure.Settings;
using Xunit;

namespace Servicedesk.Api.Tests;

/// GET /api/tickets/{id}/events/{eventId}/preview — the full note behind a
/// personal or group tag. The tag lists are scoped by recipient only, so this
/// read is the queue-access boundary: a ticket in a queue the agent can't see
/// answers like a missing one (404, and the event is never read), and only
/// the event types a tag can come from are served.
public sealed class TagNotePreviewAccessTests
{
    private static readonly Guid AllowedQueue = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000011");
    private static readonly Guid ForbiddenQueue = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000012");
    private static readonly Guid VisibleTicket = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000011");
    private static readonly Guid HiddenTicket = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000012");
    private static readonly Guid MissingTicket = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000013");

    private const long NoteEvent = 1;
    private const long InboundMailEvent = 2;
    private const long SurveyEvent = 3;
    private const long CallEvent = 4;

    private static string Url(Guid ticket, long eventId) => $"/api/tickets/{ticket}/events/{eventId}/preview";

    [Fact]
    public async Task Agent_with_queue_access_gets_the_full_note()
    {
        var (factory, host, eventReads) = Host();
        using var _f = factory; using var _h = host;
        var client = await ClientAsync(factory, host, "Agent");

        var res = await client.GetAsync(Url(VisibleTicket, NoteEvent));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Equal("Note", doc.RootElement.GetProperty("eventType").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("ticketNumber").GetInt64());
        Assert.Equal("stub", doc.RootElement.GetProperty("ticketSubject").GetString());
        Assert.Equal("<p><strong>Full</strong> note text</p>", doc.RootElement.GetProperty("bodyHtml").GetString());
        Assert.True(doc.RootElement.GetProperty("isInternal").GetBoolean());

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Url(VisibleTicket, CallEvent))).StatusCode);
    }

    [Fact]
    public async Task Ticket_in_a_hidden_queue_answers_like_a_missing_ticket_without_reading_the_event()
    {
        var (factory, host, eventReads) = Host();
        using var _f = factory; using var _h = host;
        var client = await ClientAsync(factory, host, "Agent");

        var hidden = await client.GetAsync(Url(HiddenTicket, NoteEvent));
        var missing = await client.GetAsync(Url(MissingTicket, NoteEvent));

        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        Assert.Equal(missing.StatusCode, hidden.StatusCode);
        Assert.Equal(await missing.Content.ReadAsStringAsync(), await hidden.Content.ReadAsStringAsync());
        Assert.Empty(eventReads);
    }

    [Theory]
    [InlineData(InboundMailEvent)] // not something a tag can point to
    [InlineData(SurveyEvent)]      // admin-only timeline type, and not a tag source
    [InlineData(99)]               // no such event on this ticket
    public async Task Only_tag_source_events_are_served(long eventId)
    {
        var (factory, host, _) = Host();
        using var _f = factory; using var _h = host;
        var client = await ClientAsync(factory, host, "Agent");

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(Url(VisibleTicket, eventId))).StatusCode);
    }

    [Fact]
    public async Task Customers_never_reach_the_preview()
    {
        using var factory = new SecurityBaselineFactory();
        using var host = factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.RemoveAll<NpgsqlDataSource>();
            s.AddSingleton(NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=none;Username=none;Timeout=1"));
        }));
        var client = await ClientAsync(factory, host, "Customer", amr: "pwd+mfa");

        var status = (await client.GetAsync(Url(VisibleTicket, NoteEvent))).StatusCode;

        Assert.True(status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized, $"got {status}");
    }

    // ---- host + fakes ------------------------------------------------------

    private static (SecurityBaselineFactory, WebApplicationFactory<Program>, List<long>) Host()
    {
        var eventReads = new List<long>();
        var factory = new SecurityBaselineFactory();
        var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<ITicketRepository>();
            services.AddSingleton(FakeTickets(eventReads));
            services.RemoveAll<IQueueAccessService>();
            services.AddSingleton(FakeQueueAccess());
        }));
        return (factory, host, eventReads);
    }

    private static ITicketRepository FakeTickets(List<long> eventReads)
    {
        var tickets = new Dictionary<Guid, Ticket>
        {
            [VisibleTicket] = NewTicket(VisibleTicket, AllowedQueue),
            [HiddenTicket] = NewTicket(HiddenTicket, ForbiddenQueue),
        };
        var events = new Dictionary<long, TicketEvent>
        {
            [NoteEvent] = NewEvent(NoteEvent, "Note", "<p><strong>Full</strong> note text</p>", isInternal: true),
            [InboundMailEvent] = NewEvent(InboundMailEvent, "MailReceived", "<p>mail</p>", isInternal: false),
            [SurveyEvent] = NewEvent(SurveyEvent, "SurveySubmitted", "<p>survey</p>", isInternal: true),
            [CallEvent] = NewEvent(CallEvent, "Call", "<p>call</p>", isInternal: true),
        };
        return PendingTillInPastTests.Fake<ITicketRepository>.Create((method, args) => method.Name switch
        {
            nameof(ITicketRepository.GetCoreAsync) =>
                Task.FromResult(tickets.GetValueOrDefault((Guid)args![0]!)),
            nameof(ITicketRepository.GetEventAsync) => ReadEvent((Guid)args![0]!, (long)args[1]!),
            _ => throw new NotImplementedException($"Unexpected call: {method.Name}"),
        });

        Task<TicketEvent?> ReadEvent(Guid ticketId, long eventId)
        {
            eventReads.Add(eventId);
            return Task.FromResult(ticketId == VisibleTicket ? events.GetValueOrDefault(eventId) : null);
        }
    }

    private static IQueueAccessService FakeQueueAccess()
        => PendingTillInPastTests.Fake<IQueueAccessService>.Create((method, args) => method.Name switch
        {
            nameof(IQueueAccessService.HasQueueAccessAsync) => Task.FromResult((Guid)args![2]! == AllowedQueue),
            nameof(IQueueAccessService.GetAccessibleQueueIdsAsync) =>
                Task.FromResult<IReadOnlyList<Guid>>(new[] { AllowedQueue }),
            _ => throw new NotImplementedException($"Unexpected call: {method.Name}"),
        });

    private static Ticket NewTicket(Guid id, Guid queueId) => new(
        Id: id, Number: 1, Subject: "stub", RequesterContactId: Guid.NewGuid(),
        AssigneeUserId: null, QueueId: queueId, StatusId: Guid.NewGuid(),
        PriorityId: Guid.NewGuid(), CategoryId: null, Source: "Web", ExternalRef: null,
        CreatedUtc: DateTime.UtcNow, UpdatedUtc: DateTime.UtcNow, DueUtc: null,
        FirstResponseUtc: null, ResolvedUtc: null, ClosedUtc: null, IsDeleted: false);

    private static TicketEvent NewEvent(long id, string type, string html, bool isInternal) => new(
        Id: id, TicketId: VisibleTicket, EventType: type, AuthorUserId: Guid.NewGuid(), AuthorContactId: null,
        AuthorName: "agent@example.test", BodyText: null, BodyHtml: html, MetadataJson: "{}",
        IsInternal: isInternal, CreatedUtc: DateTime.UtcNow, EditedUtc: null, EditedByUserId: null);

    private static async Task<HttpClient> ClientAsync(
        SecurityBaselineFactory factory, WebApplicationFactory<Program> host, string role, string amr = "pwd")
    {
        var userId = Guid.NewGuid();
        factory.Sessions.Roles[userId] = role;
        var sessionId = await factory.Sessions.CreateAsync(
            userId, ip: null, userAgent: null, lifetime: TimeSpan.FromHours(1), amr: amr);
        var cookieName = await factory.Settings.GetAsync<string>(SettingKeys.Security.SessionCookieName);
        var csrf = DoubleSubmitCsrfMiddleware.GenerateToken();
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add(
            "Cookie", $"{cookieName}={sessionId}; {DoubleSubmitCsrfMiddleware.CookieName}={csrf}");
        client.DefaultRequestHeaders.Add(DoubleSubmitCsrfMiddleware.HeaderName, csrf);
        return client;
    }
}
