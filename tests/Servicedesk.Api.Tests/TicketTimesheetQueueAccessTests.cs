using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
public sealed class TicketTimesheetQueueAccessTests : IClassFixture<SharedHostFixture>
{
    private readonly SharedHostFixture _hosts;

    public TicketTimesheetQueueAccessTests(SharedHostFixture hosts)
    {
        _hosts = hosts;
        hosts.Reset();
    }

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
        var factory = _hosts.Base;
        var host = WithTicketFakes(hasQueueAccess: false);
        var client = await factory.SignedInClientAsync("Agent", host);

        var response = await SendAsync(client, method, url, body);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(Handlers))]
    public async Task Agent_with_queue_access_passes(string method, string url, object? body)
    {
        var factory = _hosts.Base;
        var host = WithTicketFakes(hasQueueAccess: true);
        var client = await factory.SignedInClientAsync("Agent", host);

        var response = await SendAsync(client, method, url, body);

        Assert.True(
            response.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent,
            $"{method} {url} → {(int)response.StatusCode}");
    }

    [Theory]
    [MemberData(nameof(Handlers))]
    public async Task Missing_ticket_gets_404(string method, string url, object? body)
    {
        var factory = _hosts.Base;
        var host = WithTicketFakes(hasQueueAccess: true, ticketExists: false);
        var client = await factory.SignedInClientAsync("Agent", host);

        var response = await SendAsync(client, method, url, body);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // v0.1.12 — Timesheet.TimeAlertAllowCancel off: logged dismissals are
    // refused for everyone; only the admin silent dismiss passes.
    [Theory]
    [InlineData("Agent", false, HttpStatusCode.Conflict)]
    [InlineData("Agent", true, HttpStatusCode.Conflict)]   // silent downgraded for non-admins
    [InlineData("Admin", false, HttpStatusCode.Conflict)]
    [InlineData("Admin", true, HttpStatusCode.NoContent)]
    public async Task Dismiss_with_cancel_disabled_only_allows_admin_silent(
        string role, bool silent, HttpStatusCode expected)
    {
        var factory = _hosts.Base;
        factory.Settings.Set(SettingKeys.Timesheet.TimeAlertAllowCancel, "false");
        var host = WithTicketFakes(hasQueueAccess: true);
        var client = await factory.SignedInClientAsync(role, host);

        var url = $"/api/timesheet/ticket/{TicketId}/time-alert/dismiss" + (silent ? "?silent=true" : "");
        var response = await SendAsync(client, "POST", url, null);

        Assert.Equal(expected, response.StatusCode);
    }

    // ---- plumbing ----------------------------------------------------------

    /// The baseline host plus the ticket/queue/timesheet fakes this endpoint
    /// group injects (all Npgsql-backed in production); one host per variant.
    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> WithTicketFakes(
        bool hasQueueAccess, bool ticketExists = true)
        => _hosts.Host($"queue-access={hasQueueAccess};ticket-exists={ticketExists}",
            builder => builder.ConfigureServices(services =>
            {
                services.RemoveAll<ITicketRepository>();
                services.AddSingleton<ITicketRepository>(new StubTicketRepository(ticketExists));
                services.RemoveAll<IQueueAccessService>();
                services.AddSingleton<IQueueAccessService>((hasQueueAccess ? new FakeQueueAccess(QueueId) : FakeQueueAccess.None()));
                services.RemoveAll<ITicketTimesheetService>();
                services.AddSingleton<ITicketTimesheetService, StubTicketTimesheetService>();
                services.RemoveAll<ITicketTimeAlertService>();
                services.AddSingleton<ITicketTimeAlertService, StubTicketTimeAlertService>();
            }));

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, string method, string url, object? body)
        => method == "GET"
            ? client.GetAsync(url)
            : client.PostAsJsonAsync(url, body ?? new { });

    // ---- fakes -------------------------------------------------------------


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
                ConfirmationText: string.Empty, TrackingDisabled: false, DisableReasonPrompt: string.Empty,
                AllowCancel: true));

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
