using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Servicedesk.Api.Tests.TestInfrastructure;
using Servicedesk.Domain.Tickets;
using Servicedesk.Infrastructure.Access;
using Servicedesk.Infrastructure.Mail.Outbound;
using Servicedesk.Infrastructure.Persistence.Tickets;
using Servicedesk.Infrastructure.Tickets.Following;
using Xunit;

namespace Servicedesk.Api.Tests;

/// Every staff route scoped to one ticket answers a ticket in a queue the
/// caller cannot see exactly like a ticket that does not exist — same status,
/// same body — so an id probe learns nothing (ARCHITECTURE → Queue-based
/// access control: 404, never 403). Driven by the live route table, so a new
/// ticket route is covered the day it lands.
///
/// Gates that answer before the ticket is looked up (body validation, a
/// feature flag, a setting) give the same answer for both ids and pass; they
/// leak nothing. The agent holds every per-user feature flag so flag gates
/// don't hide the access check. Any handler that reaches the database (all
/// other data access is unreachable) fails the test.
public sealed partial class TicketRouteAccessTests : IClassFixture<SharedHostFixture>
{
    private static readonly Guid VisibleQueue = Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000a");
    private static readonly Guid HiddenQueue = Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000b");
    private static readonly Guid HiddenTicket = Guid.Parse("bbbbbbbb-0000-0000-0000-00000000000b");
    private static readonly Guid MissingTicket = Guid.Parse("bbbbbbbb-0000-0000-0000-00000000000c");
    private static readonly Guid OtherId = Guid.Parse("cccccccc-0000-0000-0000-00000000000c");

    private readonly SharedHostFixture _hosts;

    public TicketRouteAccessTests(SharedHostFixture hosts)
    {
        _hosts = hosts;
        hosts.Reset();
    }

    [Fact]
    public async Task A_ticket_in_a_hidden_queue_answers_every_ticket_route_like_a_missing_ticket()
    {
        var host = Host();
        var userId = Guid.NewGuid();
        GrantEveryFeatureFlag(_hosts.Base.Users, userId);
        var client = await _hosts.Base.SignedInClientAsync("Agent", host, userId, amr: "pwd+mfa");
        var routes = TicketRoutes(host.Services.GetRequiredService<EndpointDataSource>());

        var leaks = new StringBuilder();
        var notFound = 0;
        foreach (var (method, template) in routes)
        {
            var hidden = await SendAsync(client, method, Fill(template, HiddenTicket));
            var missing = await SendAsync(client, method, Fill(template, MissingTicket));
            if (hidden != missing)
                leaks.AppendLine($"{method} {template}: hidden → {hidden} / missing → {missing}");
            if (hidden.StartsWith("404", StringComparison.Ordinal)) notFound++;
        }

        Assert.True(leaks.Length == 0, "Ticket routes that tell a hidden ticket from a missing one:\n" + leaks);
        // Most routes must actually reach the access check (not stop at a
        // gate), or this test would prove nothing.
        Assert.True(notFound * 2 > routes.Count, $"Only {notFound} of {routes.Count} ticket routes reached the access check.");
    }

    private static async Task<string> SendAsync(HttpClient client, string method, string url)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), url);
        if (method is "POST" or "PUT" or "PATCH")
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        try
        {
            var response = await client.SendAsync(request);
            var body = TraceId().Replace(await response.Content.ReadAsStringAsync(), "");
            return $"{(int)response.StatusCode} {body}";
        }
        catch (Exception ex)
        {
            return "threw " + ex.GetType().Name; // reached a database or repository it should not
        }
    }

    private WebApplicationFactory<Program> Host() =>
        _hosts.Host("ticket-route-access", builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<NpgsqlDataSource>();
            services.AddSingleton(NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=none;Username=none;Timeout=1"));
            services.RemoveAll<IQueueAccessService>();
            services.AddSingleton<IQueueAccessService>(new FakeQueueAccess(VisibleQueue));

            // The ticket loaders. Anything else on them means a handler did
            // work before the access check.
            services.RemoveAll<ITicketRepository>();
            services.AddSingleton(Fake<ITicketRepository>.Create((m, a) => m.Name switch
            {
                nameof(ITicketRepository.GetCoreAsync) => Task.FromResult(Core((Guid)a![0]!)),
                nameof(ITicketRepository.GetByIdAsync) => Task.FromResult(Detail((Guid)a![0]!)),
                _ => throw new InvalidOperationException("Repository reached before the access check: " + m.Name),
            }));
            services.RemoveAll<ITicketTimelineReader>();
            services.AddSingleton(Fake<ITicketTimelineReader>.Create((m, a) => m.Name switch
            {
                nameof(ITicketTimelineReader.GetFirstPageAsync) => Task.FromResult(Detail((Guid)a![0]!) is { } d
                    ? new TicketTimelineFirstPage(d, false, new Dictionary<string, int>(), null, Array.Empty<TicketEvent>(), null)
                    : null),
                _ => throw new InvalidOperationException("Timeline reached before the access check: " + m.Name),
            }));
            services.RemoveAll<IRecipientSuggestionRepository>();
            services.AddSingleton(Fake<IRecipientSuggestionRepository>.Create((m, a) => m.Name switch
            {
                nameof(IRecipientSuggestionRepository.GetTicketContextAsync) => Task.FromResult(
                    (Guid)a![0]! == HiddenTicket ? new TicketMailContext { QueueId = HiddenQueue } : null),
                _ => throw new InvalidOperationException("Suggestions reached before the access check: " + m.Name),
            }));
            // Unfollow / mark-seen only touch the caller's own follow row and
            // answer 204 for any id; they never read the ticket.
            services.RemoveAll<ITicketFollowRepository>();
            services.AddSingleton(Fake<ITicketFollowRepository>.Create((m, _) => m.Name switch
            {
                nameof(ITicketFollowRepository.UnfollowAsync) or nameof(ITicketFollowRepository.MarkSeenAsync) =>
                    Task.FromResult(false),
                _ => throw new InvalidOperationException("Follow repository reached before the access check: " + m.Name),
            }));
        }));

    private static Ticket? Core(Guid id) => id == HiddenTicket
        ? new Ticket(
            Id: HiddenTicket, Number: 4242, Subject: "Hidden", RequesterContactId: OtherId,
            AssigneeUserId: null, QueueId: HiddenQueue, StatusId: OtherId, PriorityId: OtherId,
            CategoryId: null, Source: "Web", ExternalRef: null, CreatedUtc: DateTime.UtcNow, UpdatedUtc: DateTime.UtcNow,
            DueUtc: null, FirstResponseUtc: null, ResolvedUtc: null, ClosedUtc: null, IsDeleted: false)
        : null;

    private static TicketDetail? Detail(Guid id) => Core(id) is { } t
        ? new TicketDetail(t, new TicketBody(t.Id, "Hidden body", null), Array.Empty<TicketEvent>(), Array.Empty<TicketEventPin>())
        : null;

    /// Every boolean per-user flag on the fake user service, so feature-flag
    /// filters let the request through to the access check.
    private static void GrantEveryFeatureFlag(FakeUserService users, Guid userId)
    {
        foreach (var flags in typeof(FakeUserService).GetProperties()
                     .Where(p => p.PropertyType == typeof(ConcurrentDictionary<Guid, bool>))
                     .Select(p => (ConcurrentDictionary<Guid, bool>)p.GetValue(users)!))
        {
            flags[userId] = true;
        }
    }

    /// (method, template) for every route whose first segment parameter is the ticket id.
    private static List<(string Method, string Template)> TicketRoutes(EndpointDataSource source) =>
        source.Endpoints.OfType<RouteEndpoint>()
            .Select(e => (Endpoint: e, Template: "/" + e.RoutePattern.RawText!.TrimStart('/')))
            .Where(x => TicketScoped().IsMatch(x.Template))
            .SelectMany(x => (x.Endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? new[] { "GET" })
                .Select(method => (Method: method, x.Template)))
            .Distinct()
            .OrderBy(x => x.Template, StringComparer.Ordinal).ThenBy(x => x.Method, StringComparer.Ordinal)
            .ToList();

    private static string Fill(string template, Guid ticketId)
    {
        var first = true;
        return RouteParameter().Replace(template, match =>
        {
            if (first)
            {
                first = false;
                return ticketId.ToString();
            }
            return match.Groups["constraint"].Value switch
            {
                "guid" => OtherId.ToString(),
                "long" or "int" => "1",
                _ => "x",
            };
        });
    }

    [GeneratedRegex(@"^/api/(tickets|timesheet/ticket)/\{(id|ticketId)(:guid)?\}")]
    private static partial Regex TicketScoped();

    [GeneratedRegex(@"\{(?<name>[^}:?]+)(:(?<constraint>[a-z]+))?[^}]*\}")]
    private static partial Regex RouteParameter();

    [GeneratedRegex("\"traceId\":\"[^\"]*\"")]
    private static partial Regex TraceId();
}
