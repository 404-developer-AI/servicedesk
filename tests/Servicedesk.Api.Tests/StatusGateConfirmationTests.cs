using System.Text.Json;
using Servicedesk.Api.Tests.TestInfrastructure;
using Servicedesk.Domain.Companies;
using Servicedesk.Domain.Tickets;
using Servicedesk.Infrastructure.Persistence.Companies;
using Servicedesk.Infrastructure.Persistence.Tickets;
using Servicedesk.Infrastructure.Triggers.StatusGate;
using Xunit;

namespace Servicedesk.Api.Tests;

/// The ticket PATCH status-gate negotiation (v0.0.42 / v0.0.89): which
/// confirmations satisfy which gates, keep-open choices, company-link gates
/// and what is written to the timeline afterwards.
public sealed class StatusGateConfirmationTests
{
    private static readonly Guid TicketId = Guid.Parse("10000000-0000-0000-0000-0000000000a1");
    private static readonly Guid AgentId = Guid.Parse("20000000-0000-0000-0000-0000000000a1");
    private static readonly Guid ContactId = Guid.Parse("30000000-0000-0000-0000-0000000000a1");
    private static readonly Guid ActiveCompany = Guid.Parse("40000000-0000-0000-0000-0000000000a1");
    private static readonly Guid InactiveCompany = Guid.Parse("40000000-0000-0000-0000-0000000000a2");

    private readonly List<(Guid Contact, Guid Company, string Role)> _links = new();
    private readonly List<NewTicketEvent> _events = new();

    // ---- negotiation --------------------------------------------------------

    [Fact]
    public async Task A_gate_without_a_confirmation_is_missing()
    {
        var gate = Prompt(Text("reason", required: true));

        var n = await Service().NegotiateAsync(new[] { gate }, Array.Empty<GateConfirmation>(), default);

        Assert.Equal(new[] { gate }, n.Missing);
        Assert.Empty(n.Confirmed);
    }

    [Fact]
    public async Task A_required_text_question_needs_a_non_blank_answer()
    {
        var gate = Prompt(Text("reason", required: true));

        var blank = await Service().NegotiateAsync(new[] { gate }, new[] { Answer(gate, ("reason", "  ")) }, default);
        var given = await Service().NegotiateAsync(new[] { gate }, new[] { Answer(gate, ("reason", "Customer agreed")) }, default);

        Assert.Single(blank.Missing);
        var confirmed = Assert.Single(given.Confirmed);
        Assert.Equal("Customer agreed", confirmed.Answers!["reason"]);
        Assert.False(given.KeepOpen);
    }

    [Fact]
    public async Task A_choice_records_its_decision_and_keep_open_holds_the_status()
    {
        var gate = Prompt(Choice("invoice", ("Invoice now", "allow"), ("Not yet", "keep_open")));

        var keep = await Service().NegotiateAsync(new[] { gate }, new[] { Answer(gate, ("invoice", "1")) }, default);
        var allow = await Service().NegotiateAsync(new[] { gate }, new[] { Answer(gate, ("invoice", "0")) }, default);

        Assert.True(keep.KeepOpen);
        Assert.Equal(new GateChoiceDecision("invoice?", "Not yet", "keep_open"), Assert.Single(keep.Confirmed.Single().Decisions!));
        Assert.False(allow.KeepOpen);
    }

    [Theory]
    [InlineData("2")]   // out of range
    [InlineData("-1")]
    [InlineData("yes")] // not an index
    public async Task An_invalid_choice_pick_leaves_the_gate_missing(string pick)
    {
        var gate = Prompt(Choice("invoice", ("Invoice now", "allow"), ("Not yet", "keep_open")));

        var n = await Service().NegotiateAsync(new[] { gate }, new[] { Answer(gate, ("invoice", pick)) }, default);

        Assert.Single(n.Missing);
    }

    [Fact]
    public async Task A_company_link_gate_needs_a_valid_role_and_an_active_company()
    {
        var gate = CompanyLink();

        var noRole = await Service().NegotiateAsync(new[] { gate },
            new[] { new GateConfirmation(gate.TriggerId, null, ActiveCompany, "boss") }, default);
        var inactive = await Service().NegotiateAsync(new[] { gate },
            new[] { new GateConfirmation(gate.TriggerId, null, InactiveCompany, "primary") }, default);
        var ok = await Service().NegotiateAsync(new[] { gate },
            new[] { new GateConfirmation(gate.TriggerId, null, ActiveCompany, " Secondary ") }, default);

        Assert.Single(noRole.Missing);
        Assert.True(inactive.InvalidCompany);
        var link = Assert.Single(ok.Confirmed).PendingLink!;
        Assert.Equal(new PendingContactCompanyLink(ContactId, ActiveCompany, "secondary"), link);
    }

    [Fact]
    public async Task An_unknown_gate_kind_can_never_be_satisfied()
    {
        var gate = Prompt() with { Kind = "future_kind" };

        var n = await Service().NegotiateAsync(new[] { gate }, new[] { Answer(gate) }, default);

        Assert.Single(n.Missing);
    }

    // ---- side effects -------------------------------------------------------

    [Fact]
    public async Task Pending_links_are_applied_and_notes_plus_decisions_are_recorded()
    {
        var prompt = Prompt(Choice("invoice", ("Invoice now", "allow"))) with { NoteVisibility = "public" };
        var link = CompanyLink();
        var svc = Service();
        var n = await svc.NegotiateAsync(new[] { prompt, link }, new[]
        {
            Answer(prompt, ("invoice", "0")),
            new GateConfirmation(link.TriggerId, null, ActiveCompany, "primary"),
        }, default);

        await svc.ApplyPendingLinksAsync(n, default);
        var wrote = await svc.RecordAsync(TicketId, AgentId, n, default);

        Assert.True(wrote);
        Assert.Equal(new[] { (ContactId, ActiveCompany, "primary") }, _links);
        var notes = _events.Where(e => e.EventType == "Note").ToList();
        Assert.Equal(2, notes.Count);
        Assert.False(notes[0].IsInternal); // the prompt gate's note is public
        Assert.True(notes[1].IsInternal);
        Assert.All(notes, e => Assert.Equal(AgentId, e.AuthorUserId));
        var decision = Assert.Single(_events, e => e.EventType == "StatusGateDecision");
        Assert.True(decision.IsInternal);
        Assert.Equal("allow", JsonDocument.Parse(decision.MetadataJson!).RootElement.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Nothing_is_recorded_when_no_gate_matched()
    {
        var wrote = await Service().RecordAsync(TicketId, AgentId,
            new GateNegotiation(Array.Empty<MatchedStatusGate>(), Array.Empty<ConfirmedStatusGate>()), default);

        Assert.False(wrote);
        Assert.Empty(_events);
    }

    // ---- plumbing -----------------------------------------------------------

    private StatusGateConfirmationService Service() => new(
        Fake<ICompanyRepository>.Create((m, a) => m.Name switch
        {
            nameof(ICompanyRepository.GetCompanyAsync) => Task.FromResult(CompanyFor((Guid)a![0]!)),
            nameof(ICompanyRepository.UpsertContactLinkAsync) => RecordLink((Guid)a![0]!, (Guid)a[1]!, (string)a[2]!),
            _ => throw new InvalidOperationException("Unexpected: " + m.Name),
        }),
        Fake<IStatusGateService>.Create((m, a) => m.Name == nameof(IStatusGateService.RenderNoteAsync)
            ? Task.FromResult<string?>("<p>Confirmed " + ((MatchedStatusGate)a![0]!).Name + "</p>")
            : throw new InvalidOperationException("Unexpected: " + m.Name)),
        Fake<ITicketRepository>.Create((m, a) =>
        {
            if (m.Name != nameof(ITicketRepository.AddEventAsync)) throw new InvalidOperationException("Unexpected: " + m.Name);
            _events.Add((NewTicketEvent)a![1]!);
            return Task.FromResult<TicketEvent?>(null);
        }));

    private Task<ContactCompanyLink> RecordLink(Guid contact, Guid company, string role)
    {
        _links.Add((contact, company, role));
        return Task.FromResult<ContactCompanyLink>(null!);
    }

    private static Company? CompanyFor(Guid id) => id == ActiveCompany || id == InactiveCompany
        ? new Company(id, "Example Company", "", "", "", "", "", "", "", "", IsActive: id == ActiveCompany,
            DateTime.UtcNow, DateTime.UtcNow, "EXC", "Example", "", "", false, false, "once", "")
        : null;

    private static MatchedStatusGate Prompt(params GateQuestion[] questions) => new(
        Guid.NewGuid(), "Invoice check", "prompt_confirm", "Before closing", null, questions,
        "Close", "Cancel", "internal", "Confirmed: #{prompt.reason}", Guid.NewGuid(), null, null, null);

    private static MatchedStatusGate CompanyLink() => new(
        Guid.NewGuid(), "Company check", "contact_company_link", "Link the requester", null, Array.Empty<GateQuestion>(),
        "Link", "Cancel", "internal", "Linked #{company.name}", Guid.NewGuid(), null, ContactId, "Alex Example");

    private static GateQuestion Text(string key, bool required) => new(key, "text", key + "?", required, null, null);

    private static GateQuestion Choice(string key, params (string Label, string Outcome)[] options) =>
        new(key, "choice", key + "?", true, null, null, options.Select(o => new GateChoiceOption(o.Label, o.Outcome)).ToList());

    private static GateConfirmation Answer(MatchedStatusGate gate, params (string Key, string Value)[] answers) =>
        new(gate.TriggerId, answers.ToDictionary(a => a.Key, a => a.Value, StringComparer.Ordinal));
}
