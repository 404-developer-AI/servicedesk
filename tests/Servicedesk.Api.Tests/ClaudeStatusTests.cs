using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Servicedesk.Api.Auth;
using Servicedesk.Api.Tests.TestInfrastructure;
using Servicedesk.Api.Tickets;
using Servicedesk.Domain.Taxonomy;
using Servicedesk.Domain.Tickets;
using Servicedesk.Infrastructure.Access;
using Servicedesk.Infrastructure.Integrations.Claude;
using Servicedesk.Infrastructure.Persistence.Taxonomy;
using Servicedesk.Infrastructure.Persistence.Tickets;
using Servicedesk.Infrastructure.Settings;
using Servicedesk.Infrastructure.Tickets;
using Xunit;
using static Servicedesk.Api.Tests.PendingTillInPastTests;

namespace Servicedesk.Api.Tests;

/// v0.1.46 — "Status update by AI": an internal brief posted straight onto
/// the ticket as an internal note. Pins the context helpers (timed lines,
/// field changes from metadata, the budget fit that keeps the newest events)
/// and the endpoint: the note is internal, marked, posted through the shared
/// mutation path, and never posted when the call is gated or refused.
public sealed class ClaudeStatusTests
{
    private static readonly Guid TicketId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid QueueId = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly DateTime Now = new(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);

    // ---- context helpers ---------------------------------------------------

    [Fact]
    public void Text_event_line_carries_time_author_and_internal_marker()
    {
        var line = ClaudeStatusContext.FormatEvent(new StatusEventRow
        {
            EventType = "Note",
            BodyText = "  Asked the vendor for a new licence file.  ",
            IsInternal = true,
            CreatedUtc = Now,
            AuthorUserId = Guid.NewGuid(),
            UserName = "Jan",
        }, TimeZoneInfo.Utc);

        Assert.Equal("[2026-10-30 12:00] Agent Jan — Note (internal): Asked the vendor for a new licence file.", line);
    }

    [Fact]
    public void Customer_event_is_labelled_customer()
    {
        var line = ClaudeStatusContext.FormatEvent(new StatusEventRow
        {
            EventType = "MailReceived",
            BodyText = "Still broken.",
            CreatedUtc = Now,
            AuthorContactId = Guid.NewGuid(),
            ContactName = "Els Peeters",
        }, TimeZoneInfo.Utc);

        Assert.Equal("[2026-10-30 12:00] Customer Els Peeters — MailReceived: Still broken.", line);
    }

    [Fact]
    public void Field_changes_render_from_metadata()
    {
        var tz = TimeZoneInfo.Utc;
        Assert.Equal("Status changed: Open → Pending",
            ClaudeStatusContext.DescribeChange("StatusChange", """{"fromName":"Open","toName":"Pending"}""", tz));
        Assert.Equal("Assignment changed: nobody → Jan",
            ClaudeStatusContext.DescribeChange("AssignmentChange", """{"fromName":null,"toName":"Jan"}""", tz));
        Assert.Equal("Pending till changed: none → 2026-11-02 08:00",
            ClaudeStatusContext.DescribeChange("PendingTillChange", """{"from":null,"to":"2026-11-02T08:00:00Z"}""", tz));
        Assert.Equal("Call-back flag set",
            ClaudeStatusContext.DescribeChange("TicketFlagChange", """{"flag":"callback","from":false,"to":true}""", tz));
    }

    [Fact]
    public void Unreadable_change_metadata_drops_the_line()
    {
        Assert.Null(ClaudeStatusContext.DescribeChange("StatusChange", "not json", TimeZoneInfo.Utc));
        Assert.Null(ClaudeStatusContext.FormatEvent(new StatusEventRow
        {
            EventType = "StatusChange", MetadataJson = "not json", CreatedUtc = Now,
        }, TimeZoneInfo.Utc));
    }

    [Fact]
    public void Times_are_shown_in_the_server_zone()
    {
        var brussels = TimeZoneInfo.FindSystemTimeZoneById("Europe/Brussels");
        // 30 Oct 2026 is after the DST switch: UTC+1.
        Assert.Equal("2026-10-30 13:00", ClaudeStatusContext.FormatLocal(Now, brussels));
    }

    [Fact]
    public void Timeline_that_fits_is_kept_whole()
    {
        var lines = new[] { "a", "b", "c" };
        Assert.Equal("a\nb\nc\n", ClaudeStatusContext.FitTimeline(lines, 100));
    }

    [Fact]
    public void Timeline_over_budget_keeps_the_first_and_the_newest_events()
    {
        var lines = Enumerable.Range(1, 50).Select(i => $"event {i:00} " + new string('x', 40)).ToList();

        var fitted = ClaudeStatusContext.FitTimeline(lines, 600);

        Assert.True(fitted.Length <= 600, $"length {fitted.Length}");
        Assert.StartsWith("event 01 ", fitted);
        Assert.Contains("earlier events left out", fitted);
        Assert.EndsWith("event 50 " + new string('x', 40) + "\n", fitted);
        Assert.DoesNotContain("event 02 ", fitted);
    }

    [Fact]
    public void Status_settings_have_default_registrations()
    {
        Assert.Contains(SettingDefaults.All, d => d.Key == SettingKeys.Claude.StatusTemplate && d.Value.Contains("- "));
        Assert.Contains(SettingDefaults.All, d => d.Key == SettingKeys.Claude.StatusSystemPrompt && d.Value.Length > 0);
        Assert.Contains(SettingDefaults.All, d => d.Key == SettingKeys.Claude.StatusMaxContextChars && int.Parse(d.Value) >= 1000);
    }

    [Fact]
    public void Note_opens_with_the_summary_by_ai_heading_and_is_marked()
    {
        Assert.Equal("<p><strong>Summary by AI</strong></p><p>x</p>",
            TicketClaudeEndpoints.BuildStatusNoteHtml("<p>x</p>"));
        using var doc = JsonDocument.Parse(TicketClaudeEndpoints.StatusNoteMetadataJson);
        Assert.Equal("status", doc.RootElement.GetProperty("aiGenerated").GetString());
    }

    // ---- endpoint ------------------------------------------------------------

    [Fact]
    public async Task Status_update_posts_an_internal_marked_note_through_the_mutation_path()
    {
        var calls = new List<string>();
        NewTicketEvent? posted = null;
        var mutations = Fake<ITicketMutationService>.Create((m, args) =>
        {
            calls.Add(m.Name);
            return m.Name switch
            {
                "PrecheckAccessAsync" => Task.FromResult(new AccessPrecheck(TicketMutationCheck.Ok, Ticket())),
                "AddEventAsync" => Task.FromResult<TicketEvent?>(Event(posted = (NewTicketEvent)args![1]!)),
                "PublishEventAsync" => Task.CompletedTask,
                _ => Unexpected(m),
            };
        });

        using var factory = new SecurityBaselineFactory();
        var client = await ClientAsync(factory, Ok("**Stand van zaken:**\n\n- Waiting on the customer."), mutations);

        var response = await client.PostAsJsonAsync($"/api/tickets/{TicketId}/ai-status", new { });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(new[] { "PrecheckAccessAsync", "AddEventAsync", "PublishEventAsync" }, calls);
        Assert.NotNull(posted);
        Assert.Equal("Note", posted!.EventType);
        Assert.True(posted.IsInternal);
        Assert.Contains("aiGenerated", posted.MetadataJson);
        Assert.StartsWith("<p><strong>Summary by AI</strong></p>", posted.BodyHtml);
        Assert.Contains("Waiting on the customer.", posted.BodyHtml);
        Assert.Contains(factory.Audit.Events, e => e.EventType == ClaudeEventTypes.StatusRequested);
    }

    [Fact]
    public async Task Refused_or_over_budget_status_update_posts_nothing()
    {
        var mutations = Fake<ITicketMutationService>.Create((m, _) => Unexpected(m));

        using var factory = new SecurityBaselineFactory();
        var client = await ClientAsync(factory, new ClaudeProposalResult(
            ClaudeProposalOutcome.BudgetExceeded, null, null, "Budget exhausted.", 0, 0, 0, 0, 10, 10), mutations);

        var response = await client.PostAsJsonAsync($"/api/tickets/{TicketId}/ai-status", new { });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Queue_with_ai_off_refuses_before_calling_the_model()
    {
        var mutations = Fake<ITicketMutationService>.Create((m, _) => Unexpected(m));
        var assist = Fake<IClaudeAssistService>.Create((m, _) => Unexpected(m));

        using var factory = new SecurityBaselineFactory();
        var client = await ClientAsync(factory, assist, mutations, aiOnQueue: false);

        var response = await client.PostAsJsonAsync($"/api/tickets/{TicketId}/ai-status", new { });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("queue_disabled", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Ticket_outside_the_agents_queues_is_a_404()
    {
        var assist = Fake<IClaudeAssistService>.Create((m, _) => Unexpected(m));
        var mutations = Fake<ITicketMutationService>.Create((m, _) => Unexpected(m));

        using var factory = new SecurityBaselineFactory();
        var client = await ClientAsync(factory, assist, mutations, hasQueueAccess: false);

        var response = await client.PostAsJsonAsync($"/api/tickets/{TicketId}/ai-status", new { });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- plumbing ------------------------------------------------------------

    private static ClaudeProposalResult Ok(string text) => new(
        ClaudeProposalOutcome.Ok, text, ClaudeMarkdown.MarkdownToHtml(text), null, 100, 50, 1234, 0, 1234, 10_000_000);

    private static Task<HttpClient> ClientAsync(
        SecurityBaselineFactory factory, ClaudeProposalResult result, ITicketMutationService mutations) =>
        ClientAsync(factory, Fake<IClaudeAssistService>.Create((m, _) => m.Name == "GenerateStatusAsync"
            ? Task.FromResult(result)
            : Unexpected(m)), mutations);

    private static async Task<HttpClient> ClientAsync(
        SecurityBaselineFactory factory,
        IClaudeAssistService assist,
        ITicketMutationService mutations,
        bool aiOnQueue = true,
        bool hasQueueAccess = true)
    {
        var host = factory.WithWebHostBuilder(b => b.ConfigureServices(services =>
        {
            services.RemoveAll<ITicketRepository>();
            services.AddSingleton(Fake<ITicketRepository>.Create((m, args) => m.Name == "GetCoreAsync"
                ? Task.FromResult<Ticket?>(Ticket())
                : Unexpected(m)));
            services.RemoveAll<IQueueAccessService>();
            services.AddSingleton(Fake<IQueueAccessService>.Create((m, _) => m.Name == "HasQueueAccessAsync"
                ? Task.FromResult(hasQueueAccess)
                : Unexpected(m)));
            services.RemoveAll<ITaxonomyRepository>();
            services.AddSingleton(Fake<ITaxonomyRepository>.Create((m, _) => m.Name == "GetQueueAsync"
                ? Task.FromResult<Queue?>(new Queue(QueueId, "Support", "support", "", "#000", "inbox", 0, true, false, Now, Now,
                    AiAssistEnabled: aiOnQueue))
                : Unexpected(m)));
            services.RemoveAll<IClaudeAssistService>();
            services.AddSingleton(assist);
            services.RemoveAll<ITicketMutationService>();
            services.AddSingleton(mutations);
        }));

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

    private static Ticket Ticket() => new(
        TicketId, 20001, "Subject", Guid.NewGuid(), null, QueueId, Guid.NewGuid(), Guid.NewGuid(), null,
        "web", null, Now, Now, null, null, null, null, false);

    private static TicketEvent Event(NewTicketEvent input) => new(
        1, TicketId, input.EventType, input.AuthorUserId, null, "Agent", input.BodyText, input.BodyHtml,
        input.MetadataJson ?? "{}", input.IsInternal, Now, null, null);

    private static object Unexpected(MethodInfo m) =>
        throw new InvalidOperationException($"Unexpected call: {m.DeclaringType?.Name}.{m.Name}");
}
