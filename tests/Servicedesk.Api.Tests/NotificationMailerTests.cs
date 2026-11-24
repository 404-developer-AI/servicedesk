using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Servicedesk.Api.Tests.TestInfrastructure;
using Servicedesk.Domain.Taxonomy;
using Servicedesk.Infrastructure.Auth;
using Servicedesk.Infrastructure.Mail.Graph;
using Servicedesk.Infrastructure.Notifications;
using Servicedesk.Infrastructure.Persistence.Taxonomy;
using Servicedesk.Infrastructure.Settings;
using Xunit;

namespace Servicedesk.Api.Tests;

/// The shared notification mailer sends one mail per recipient side by side
/// (Logs10: the sends ran one after the other inside the agent's POST),
/// never more than four at a time (Exchange's per-mailbox limit), and still
/// stamps every bell row — sent, opted out or failed.
public sealed class NotificationMailerTests
{
    private static readonly Guid QueueId = Guid.Parse("a0000000-0000-0000-0000-00000000000a");
    private static readonly DateTime Now = new(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Sends_side_by_side_but_never_more_than_four_at_once()
    {
        var w = new World();
        var rows = Enumerable.Range(0, 9).Select(_ => w.Row()).ToList();

        await w.Mailer.SendForRowsAsync(rows, Content(), new("mention", DefaultOn: true), default);

        Assert.Equal(9, w.Sent);
        Assert.Equal(4, w.MaxInFlight);
        Assert.All(rows, r => Assert.Null(w.Stamps[r.Id]));
    }

    [Fact]
    public async Task Every_row_is_stamped_when_some_sends_fail_or_are_opted_out()
    {
        var w = new World();
        var ok = w.Row();
        var failing = w.Row(email: "broken@example.test");
        var optedOut = w.Row();
        w.OptedOut.Add(optedOut.UserId);

        await w.Mailer.SendForRowsAsync([ok, failing, optedOut], Content(), new("mention", DefaultOn: true), default);

        Assert.Null(w.Stamps[ok.Id]);
        Assert.Equal("mailbox unavailable", w.Stamps[failing.Id]);
        Assert.Equal("mail turned off in profile", w.Stamps[optedOut.Id]);
        Assert.Equal(1, w.Sent);
    }

    [Fact]
    public async Task Tagging_mailboxes_are_sent_side_by_side_too()
    {
        var w = new World();

        await w.Mailer.SendToAddressesAsync(
            Enumerable.Range(0, 6).Select(i => $"box{i}@example.test").ToList(), Content(), default);

        Assert.Equal(6, w.Sent);
        Assert.Equal(4, w.MaxInFlight);
    }

    private static NotificationMailContent Content() => new(
        QueueId, Guid.NewGuid(), 1042, "Printer offline", "Tagged", "You were tagged",
        "agent@example.test", "tagged you.", "Can you have a look?", "/tickets/x#event-1");

    private sealed class World
    {
        private readonly Dictionary<Guid, string> _emails = new();
        private int _inFlight;
        private int _sent;
        private int _maxInFlight;

        public HashSet<Guid> OptedOut { get; } = new();
        public ConcurrentDictionary<Guid, string?> Stamps { get; } = new();
        public int Sent => _sent;
        public int MaxInFlight => _maxInFlight;

        public UserNotificationRow Row(string? email = null)
        {
            var userId = Guid.NewGuid();
            _emails[userId] = email ?? $"user{_emails.Count}@example.test";
            return new UserNotificationRow(Guid.NewGuid(), userId, null, null, "mention", Guid.NewGuid(), 1042,
                "Printer offline", 1, "Note", "", Now, null, null, null, null);
        }

        private NotificationMailer? _mailer;
        public NotificationMailer Mailer => _mailer ??= new NotificationMailer(
            Fake<INotificationRepository>.Create((m, a) => m.Name switch
            {
                nameof(INotificationRepository.ListMailOptOutsAsync) => Task.FromResult<IReadOnlySet<Guid>>(OptedOut),
                nameof(INotificationRepository.MarkEmailSentAsync) => Stamp((Guid)a![0]!, (string?)a[2]),
                _ => throw new InvalidOperationException("Unexpected repository call: " + m.Name),
            }),
            Fake<IUserService>.Create((_, a) => Task.FromResult<ApplicationUser?>(new ApplicationUser(
                (Guid)a![0]!, _emails[(Guid)a[0]!], null, "Agent", Now.AddYears(-1), null, 0, null, "Local", null, null,
                IsActive: true))),
            Fake<ITaxonomyRepository>.Create((_, _) => Task.FromResult<Queue?>(new Queue(
                QueueId, "Support", "support", "", "#000", "inbox", 0, true, false, Now, Now,
                OutboundMailboxAddress: "support@example.test"))),
            Fake<IGraphMailClient>.Create((m, a) => m.Name == nameof(IGraphMailClient.SendMailAsync)
                ? SendAsync((GraphOutboundMessage)a![0]!)
                : throw new InvalidOperationException("Unexpected Graph call: " + m.Name)),
            new InMemorySettingsService(),
            NullLogger<NotificationMailer>.Instance);

        private Task Stamp(Guid id, string? error)
        {
            Stamps[id] = error;
            return Task.CompletedTask;
        }

        private async Task<GraphSentMailResult> SendAsync(GraphOutboundMessage message)
        {
            var now = Interlocked.Increment(ref _inFlight);
            int seen;
            while (now > (seen = _maxInFlight) && Interlocked.CompareExchange(ref _maxInFlight, now, seen) != seen) { }
            try
            {
                await Task.Delay(40);
                if (message.To[0].Address == "broken@example.test")
                    throw new InvalidOperationException("mailbox unavailable");
                Interlocked.Increment(ref _sent);
                return new GraphSentMailResult("<id@example.test>", Now);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }
    }
}
