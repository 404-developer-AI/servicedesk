using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Servicedesk.Api.Auth;
using Servicedesk.Api.Tests.TestInfrastructure;
using Servicedesk.Domain.Tickets;
using Servicedesk.Infrastructure.Access;
using Servicedesk.Infrastructure.Persistence.Tickets;
using Servicedesk.Infrastructure.Settings;
using Servicedesk.Infrastructure.Timesheet;
using Xunit;

namespace Servicedesk.Api.Tests;

/// v0.1.3 (audit v0.1.1 #5) — the ticket-scoped timesheet/time-alert
/// endpoints enforce queue access like every other ticket-scoped surface.
/// An agent without access to the ticket's queue gets 404 (never 403, so
/// ticket existence does not leak) on every handler, reads AND writes.
public sealed class TicketTimesheetQueueAccessTests
{
    private static readonly Guid TicketId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid QueueId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    public static readonly TheoryData<string, string, object?> Handlers = new()
    {
        { "GET", $"/api/timesheet/ticket/{TicketId}", null },
        { "GET", $"/api/timesheet/ticket/{TicketId}/reply-html", null },
        { "GET", $"/api/timesheet/ticket/{TicketId}/time-alert", null },
        { "POST", $"/api/timesheet/ticket/{TicketId}/time-alert/dismiss", null },
        { "POST", $"/api/timesheet/ticket/{TicketId}/time-alert/extend", new { addMinutes = 60, customerConfirmed = true, note = (string?)null } },
        { "POST", $"/api/timesheet/ticket/{TicketId}/time-alert/disable", new { reason = "test" } },
    };

    [Theory]
    [MemberData(nameof(Handlers))]
    public async Task Agent_without_queue_access_gets_404(string method, string url, object? body)
    {
        using var factory = new SecurityBaselineFactory();
        using var host = WithTicketFakes(factory, hasQueueAccess: false);
        var client = await AgentClientAsync(factory, host);

        var response = await SendAsync(client, method, url, body);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(Handlers))]
    public async Task Agent_with_queue_access_passes(string method, string url, object? body)
    {
        using var factory = new SecurityBaselineFactory();
        using var host = WithTicketFakes(factory, hasQueueAccess: true);
        var client = await AgentClientAsync(factory, host);

        var response = await SendAsync(client, method, url, body);

        Assert.True(
            response.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent,
            $"{method} {url} → {(int)response.StatusCode}");
    }

    [Theory]
    [MemberData(nameof(Handlers))]
    public async Task Missing_ticket_gets_404(string method, string url, object? body)
    {
        using var factory = new SecurityBaselineFactory();
        using var host = WithTicketFakes(factory, hasQueueAccess: true, ticketExists: false);
        var client = await AgentClientAsync(factory, host);

        var response = await SendAsync(client, method, url, body);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- plumbing ----------------------------------------------------------

    private static WebApplicationFactoryDerived WithTicketFakes(
        SecurityBaselineFactory factory, bool hasQueueAccess, bool ticketExists = true)
        => new(factory, hasQueueAccess, ticketExists);

    /// Wraps the baseline factory with the ticket/queue/timesheet fakes this
    /// endpoint group injects (all Npgsql-backed in production).
    public sealed class WebApplicationFactoryDerived : IDisposable
    {
        private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _inner;

        public WebApplicationFactoryDerived(SecurityBaselineFactory factory, bool hasQueueAccess, bool ticketExists)
        {
            _inner = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.RemoveAll<ITicketRepository>();
                services.AddSingleton<ITicketRepository>(new StubTicketRepository(ticketExists));
                services.RemoveAll<IQueueAccessService>();
                services.AddSingleton<IQueueAccessService>(new StubQueueAccessService(hasQueueAccess));
                services.RemoveAll<ITicketTimesheetService>();
                services.AddSingleton<ITicketTimesheetService, StubTicketTimesheetService>();
                services.RemoveAll<ITicketTimeAlertService>();
                services.AddSingleton<ITicketTimeAlertService, StubTicketTimeAlertService>();
            }));
        }

        public HttpClient CreateClient() => _inner.CreateClient();

        public void Dispose() => _inner.Dispose();
    }

    private static async Task<HttpClient> AgentClientAsync(SecurityBaselineFactory factory, WebApplicationFactoryDerived host)
    {
        var userId = Guid.NewGuid();
        factory.Sessions.Roles[userId] = "Agent";
        var sessionId = await factory.Sessions.CreateAsync(
            userId, ip: null, userAgent: null, lifetime: TimeSpan.FromHours(1), amr: "pwd");
        var cookieName = await factory.Settings.GetAsync<string>(SettingKeys.Security.SessionCookieName);
        var csrf = DoubleSubmitCsrfMiddleware.GenerateToken();
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add(
            "Cookie", $"{cookieName}={sessionId}; {DoubleSubmitCsrfMiddleware.CookieName}={csrf}");
        client.DefaultRequestHeaders.Add(DoubleSubmitCsrfMiddleware.HeaderName, csrf);
        return client;
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, string method, string url, object? body)
        => method == "GET"
            ? client.GetAsync(url)
            : client.PostAsJsonAsync(url, body ?? new { });

    // ---- fakes -------------------------------------------------------------

    private sealed class StubQueueAccessService(bool allow) : IQueueAccessService
    {
        public Task<IReadOnlyList<Guid>> GetAccessibleQueueIdsAsync(Guid userId, string role, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Guid>>(allow ? new[] { QueueId } : Array.Empty<Guid>());

        public Task<bool> HasQueueAccessAsync(Guid userId, string role, Guid queueId, CancellationToken ct = default)
            => Task.FromResult(allow && queueId == QueueId);

        public Task SetQueueAccessAsync(Guid userId, IReadOnlyList<Guid> queueIds, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<Guid>> GetUsersForQueueAsync(Guid queueId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Guid>>(Array.Empty<Guid>());

        public void InvalidateCache(Guid userId) { }
    }

    private sealed class StubTicketTimesheetService : ITicketTimesheetService
    {
        public Task<IReadOnlyList<TimesheetEntryRow>> ListByTicketAsync(Guid ticketId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<TimesheetEntryRow>>(Array.Empty<TimesheetEntryRow>());

        public Task<string> BuildReplyHtmlAsync(Guid ticketId, CancellationToken ct = default)
            => Task.FromResult(string.Empty);
    }

    private sealed class StubTicketTimeAlertService : ITicketTimeAlertService
    {
        public Task<TicketTimeAlertStatus> GetStatusAsync(Guid ticketId, CancellationToken ct = default)
            => Task.FromResult(new TicketTimeAlertStatus(
                Enabled: false, ThresholdMinutes: 0, ExtraMinutes: 0, LimitMinutes: 0,
                TotalMinutes: 0, RemainingMinutes: 0, Exceeded: false, DefaultExtraMinutes: 0,
                ConfirmationText: string.Empty, TrackingDisabled: false, DisableReasonPrompt: string.Empty));

        public Task DismissAsync(Guid ticketId, Guid actorUserId, bool silent = false, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<TicketTimeAlertExtendResult> ExtendAsync(
            Guid ticketId, Guid actorUserId, int addMinutes, bool customerConfirmed, string? note, CancellationToken ct = default)
            => Task.FromResult(TicketTimeAlertExtendResult.Ok);

        public Task<TicketTimeAlertDisableResult> DisableAsync(
            Guid ticketId, Guid actorUserId, string reason, CancellationToken ct = default)
            => Task.FromResult(TicketTimeAlertDisableResult.Ok);
    }

    /// Only GetByIdAsync is reachable through these endpoints; every other
    /// member throws so an unexpected call path fails loudly.
    private sealed class StubTicketRepository(bool ticketExists) : ITicketRepository
    {
        public Task<TicketDetail?> GetByIdAsync(Guid id, CancellationToken ct)
        {
            if (!ticketExists || id != TicketId) return Task.FromResult<TicketDetail?>(null);
            var ticket = new Ticket(
                Id: TicketId, Number: 1, Subject: "stub", RequesterContactId: Guid.NewGuid(),
                AssigneeUserId: null, QueueId: QueueId, StatusId: Guid.NewGuid(),
                PriorityId: Guid.NewGuid(), CategoryId: null, Source: "Web", ExternalRef: null,
                CreatedUtc: DateTime.UtcNow, UpdatedUtc: DateTime.UtcNow, DueUtc: null,
                FirstResponseUtc: null, ResolvedUtc: null, ClosedUtc: null, IsDeleted: false);
            var detail = new TicketDetail(
                ticket,
                new TicketBody(TicketId, string.Empty, null),
                Array.Empty<TicketEvent>(),
                Array.Empty<TicketEventPin>());
            return Task.FromResult<TicketDetail?>(detail);
        }

        public Task<TicketPage> SearchAsync(TicketQuery query, VisibilityScope scope, Guid? viewerUserId, Guid? viewerCompanyId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Ticket> CreateAsync(NewTicket input, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<TicketDetail?> UpdateFieldsAsync(Guid ticketId, TicketFieldUpdate update, Guid actorUserId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<TicketDetail?> AssignCompanyAsync(Guid ticketId, Guid companyId, Guid actorUserId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<TicketDetail?> ChangeRequesterAsync(Guid ticketId, Guid newContactId, Guid? newCompanyId, bool awaitingCompanyAssignment, string? companyResolvedVia, Guid actorUserId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<TicketEvent?> AddEventAsync(Guid ticketId, NewTicketEvent input, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<bool> IsTitleReviewedAsync(Guid ticketId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<bool> MarkTitleReviewedAsync(Guid ticketId, Guid actorUserId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<TicketEvent?> UpdateEventAsync(Guid ticketId, long eventId, UpdateTicketEvent input, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<TicketEventRevision>> GetEventRevisionsAsync(Guid ticketId, long eventId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<TicketEventPin?> PinEventAsync(Guid ticketId, long eventId, Guid userId, string remark, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<bool> UnpinEventAsync(Guid ticketId, long eventId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<TicketEventPin?> UpdatePinRemarkAsync(Guid ticketId, long eventId, string remark, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<bool> EventBelongsToTicketAsync(Guid ticketId, long eventId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<IReadOnlyDictionary<Guid, int>> GetOpenCountsByQueueAsync(CancellationToken ct)
            => throw new NotImplementedException();
        public Task<int> InsertFakeBatchAsync(int count, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<TicketPickerHit>> SearchPickerAsync(string? search, Guid excludeTicketId, IReadOnlyCollection<Guid>? accessibleQueueIds, Guid? recentForUserId, int limit, CancellationToken ct, bool projectsOnly = false)
            => throw new NotImplementedException();
        public Task<TicketDetailRelations?> GetDetailRelationsAsync(Guid ticketId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<MergeResult?> MergeAsync(Guid sourceTicketId, Guid targetTicketId, Guid actorUserId, bool acknowledgedCrossCustomer, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<SplitResult?> SplitAsync(Guid sourceTicketId, long sourceMailEventId, string newSubject, Guid actorUserId, string? overrideBodyHtml, string? overrideBodyText, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<LinkParentResult> LinkParentAsync(Guid ticketId, Guid parentTicketId, Guid actorUserId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<bool> UnlinkParentAsync(Guid ticketId, Guid actorUserId, CancellationToken ct)
            => throw new NotImplementedException();
    }
}
