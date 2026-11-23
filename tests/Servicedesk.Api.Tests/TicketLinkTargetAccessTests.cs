using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Servicedesk.Api.Tests.TestInfrastructure;
using Servicedesk.Domain.Tickets;
using Servicedesk.Infrastructure.Access;
using Servicedesk.Infrastructure.Persistence.Tickets;
using Servicedesk.Infrastructure.Settings;
using Xunit;

namespace Servicedesk.Api.Tests;

/// Endpoints that take a second ticket id in the body (merge target, parent,
/// project) answer a ticket in a queue the caller can't see exactly like a
/// missing one — same status, same body — so an id probe cannot tell
/// "exists but not yours" from "does not exist" (TicketAccess: never 403).
public sealed class TicketLinkTargetAccessTests : IClassFixture<SharedHostFixture>
{
    private readonly SharedHostFixture _hosts;

    public TicketLinkTargetAccessTests(SharedHostFixture hosts)
    {
        _hosts = hosts;
        hosts.Reset();
    }

    private static readonly Guid AllowedQueue = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid ForbiddenQueue = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid SourceId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly Guid HiddenId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid MissingId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000003");

    public static readonly TheoryData<string, string> Endpoints = new()
    {
        { $"/api/tickets/{SourceId}/merge", "targetTicketId" },
        { $"/api/tickets/{SourceId}/link-parent", "parentTicketId" },
        { $"/api/tickets/{SourceId}/project/link", "projectTicketId" },
    };

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Ticket_in_a_hidden_queue_answers_like_a_missing_ticket(string url, string field)
    {
        var factory = _hosts.Base;
        factory.Settings.Set(SettingKeys.Projects.Enabled, "true");
        var host = _hosts.Host("link-target-fakes", builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<ITicketRepository>();
            services.AddSingleton(FakeTickets());
            services.RemoveAll<IQueueAccessService>();
            services.AddSingleton(FakeQueueAccess());
            // Never reached: the target check must answer first.
            services.RemoveAll<ITicketProjectRepository>();
            services.AddSingleton(Fake<ITicketProjectRepository>.Create(
                (method, _) => throw new NotImplementedException($"Unexpected call: {method.Name}")));
        }));
        var client = await factory.SignedInClientAsync("Agent", host);

        var hidden = await client.PostAsJsonAsync(url, new Dictionary<string, object> { [field] = HiddenId });
        var missing = await client.PostAsJsonAsync(url, new Dictionary<string, object> { [field] = MissingId });

        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal(missing.StatusCode, hidden.StatusCode);
        Assert.Equal(await missing.Content.ReadAsStringAsync(), await hidden.Content.ReadAsStringAsync());
    }

    // ---- fakes -------------------------------------------------------------

    private static ITicketRepository FakeTickets()
    {
        var byId = new Dictionary<Guid, Ticket>
        {
            [SourceId] = NewTicket(SourceId, AllowedQueue),
            [HiddenId] = NewTicket(HiddenId, ForbiddenQueue),
        };
        return Fake<ITicketRepository>.Create((method, args) => method.Name switch
        {
            nameof(ITicketRepository.GetCoreAsync) =>
                Task.FromResult(byId.GetValueOrDefault((Guid)args![0]!)),
            _ => throw new NotImplementedException($"Unexpected call: {method.Name}"),
        });
    }

    private static IQueueAccessService FakeQueueAccess()
        => Fake<IQueueAccessService>.Create((method, args) => method.Name switch
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
}
