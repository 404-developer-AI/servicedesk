using Microsoft.Extensions.Logging.Abstractions;
using Servicedesk.Domain.Taxonomy;
using Servicedesk.Infrastructure.Auth;
using Servicedesk.Infrastructure.Mail.Graph;
using Servicedesk.Infrastructure.Notifications;
using Servicedesk.Infrastructure.Persistence.Taxonomy;
using Servicedesk.Infrastructure.Realtime;
using Servicedesk.Infrastructure.Settings;
using Xunit;

namespace Servicedesk.Api.Tests;

/// v0.0.12 stap 4 — pins the four delivery paths of
/// <see cref="MentionNotificationService"/>: persistent row, SignalR push,
/// mail-send, and failure isolation. The happy path proves all three
/// channels fan out in order; the kill-switch / no-mailbox / Graph-throws
/// cases prove the row + push still land when mail can't.
public sealed class MentionNotificationServiceTests
{
    private static readonly Guid TicketId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid QueueId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid SourceUserId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid RecipientId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    [Fact]
    public async Task Happy_path_inserts_row_pushes_signalr_and_sends_mail()
    {
        var (svc, repo, notifier, graph, _) = Build(
            recipientEmail: "bob@example.com",
            mailbox: "support@desk.test");

        await svc.PublishAsync(MakeSource(new[] { RecipientId }), default);

        var row = Assert.Single(repo.Rows);
        Assert.Equal(RecipientId, row.UserId);
        Assert.Equal("mention", row.NotificationType);

        var pushed = Assert.Single(notifier.Pushed);
        Assert.Equal(RecipientId, pushed.UserId);
        Assert.Equal(row.Id, pushed.Payload.Id);

        var sent = Assert.Single(graph.Sent);
        Assert.Equal("support@desk.test", sent.FromMailbox);
        Assert.Equal("bob@example.com", Assert.Single(sent.To).Address);
        Assert.StartsWith("Tagged: ", sent.Subject);
        Assert.Contains($"[#{42}]", sent.Subject);

        // email_sent_utc stamped — verified via the last MarkEmailSent call.
        var stamp = Assert.Single(repo.EmailStamps);
        Assert.NotNull(stamp.SentUtc);
        Assert.Null(stamp.Error);
    }

    [Fact]
    public async Task Mail_kill_switch_off_skips_graph_but_still_persists_and_pushes()
    {
        var (svc, repo, notifier, graph, settings) = Build(
            recipientEmail: "bob@example.com",
            mailbox: "support@desk.test");
        settings.Set(SettingKeys.Notifications.MentionEmailEnabled, "false");

        await svc.PublishAsync(MakeSource(new[] { RecipientId }), default);

        Assert.Single(repo.Rows);
        Assert.Single(notifier.Pushed);
        Assert.Empty(graph.Sent);
        Assert.Empty(repo.EmailStamps); // no per-row stamp either
    }

    [Fact]
    public async Task No_mailbox_configured_marks_email_error_and_skips_graph()
    {
        var (svc, repo, notifier, graph, _) = Build(
            recipientEmail: "bob@example.com",
            mailbox: null); // queue has neither outbound nor inbound mailbox

        await svc.PublishAsync(MakeSource(new[] { RecipientId }), default);

        Assert.Single(repo.Rows);
        Assert.Single(notifier.Pushed);
        Assert.Empty(graph.Sent);
        var stamp = Assert.Single(repo.EmailStamps);
        Assert.Null(stamp.SentUtc);
        Assert.Equal("no mailbox configured on queue", stamp.Error);
    }

    [Fact]
    public async Task Graph_throwing_is_isolated_and_captured_in_email_error()
    {
        var (svc, repo, notifier, graph, _) = Build(
            recipientEmail: "bob@example.com",
            mailbox: "support@desk.test");
        graph.ThrowOnSend = true;

        // The service must not propagate the throw — the caller already
        // posted the ticket-event, we don't undo that over a mail failure.
        await svc.PublishAsync(MakeSource(new[] { RecipientId }), default);

        Assert.Single(repo.Rows);
        Assert.Single(notifier.Pushed);
        var stamp = Assert.Single(repo.EmailStamps);
        Assert.Null(stamp.SentUtc);
        Assert.False(string.IsNullOrWhiteSpace(stamp.Error));
    }

    [Fact]
    public async Task Empty_mentioned_user_ids_is_a_noop()
    {
        var (svc, repo, notifier, graph, _) = Build(
            recipientEmail: "bob@example.com",
            mailbox: "support@desk.test");

        await svc.PublishAsync(MakeSource(Array.Empty<Guid>()), default);

        Assert.Empty(repo.Rows);
        Assert.Empty(notifier.Pushed);
        Assert.Empty(graph.Sent);
    }

    // ---- helpers ----

    private static MentionNotificationSource MakeSource(IReadOnlyList<Guid> mentioned) =>
        new(
            TicketId: TicketId,
            TicketNumber: 42,
            TicketSubject: "Laptop won't boot",
            QueueId: QueueId,
            EventId: 100,
            EventType: "Note",
            SourceUserId: SourceUserId,
            SourceUserEmail: "alice@desk.test",
            MentionedUserIds: mentioned,
            BodyHtml: "<p>Please take a look @@bob</p>",
            BodyText: "Please take a look @bob");

    private static (
        MentionNotificationService svc,
        StubRepo repo,
        StubNotifier notifier,
        StubGraph graph,
        StubSettings settings) Build(string recipientEmail, string? mailbox)
    {
        var repo = new StubRepo();
        var notifier = new StubNotifier();
        var users = new StubUsers(recipientEmail);
        var taxonomy = new StubTaxonomy(mailbox);
        var graph = new StubGraph();
        var settings = new StubSettings();
        var svc = new MentionNotificationService(repo, notifier, users, taxonomy, graph, settings,
            NullLogger<MentionNotificationService>.Instance);
        return (svc, repo, notifier, graph, settings);
    }

    private sealed class StubRepo : INotificationRepository
    {
        public List<UserNotificationRow> Rows { get; } = new();
        public List<(Guid Id, DateTime? SentUtc, string? Error)> EmailStamps { get; } = new();

        public Task<IReadOnlyList<UserNotificationRow>> CreateManyAsync(IReadOnlyList<NewUserNotification> rows, CancellationToken ct)
        {
            var now = DateTime.UtcNow;
            foreach (var r in rows)
            {
                Rows.Add(new UserNotificationRow(
                    Id: Guid.NewGuid(),
                    UserId: r.UserId,
                    SourceUserId: r.SourceUserId,
                    SourceUserEmail: "alice@desk.test",
                    NotificationType: r.NotificationType,
                    TicketId: r.TicketId,
                    TicketNumber: r.TicketNumber,
                    TicketSubject: r.TicketSubject,
                    EventId: r.EventId,
                    EventType: r.EventType,
                    PreviewText: r.PreviewText,
                    CreatedUtc: now,
                    ViewedUtc: null,
                    AckedUtc: null,
                    EmailSentUtc: null,
                    EmailError: null));
            }
            return Task.FromResult<IReadOnlyList<UserNotificationRow>>(Rows.ToList());
        }

        public Task<IReadOnlyList<UserNotificationRow>> ListPendingForUserAsync(Guid userId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<UserNotificationRow>>(Array.Empty<UserNotificationRow>());
        public Task<IReadOnlyList<UserNotificationRow>> ListHistoryForUserAsync(Guid userId, NotificationHistoryCursor? cursor, int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<UserNotificationRow>>(Array.Empty<UserNotificationRow>());
        public Task<UserNotificationRow?> GetByIdForUserAsync(Guid id, Guid userId, CancellationToken ct) =>
            Task.FromResult<UserNotificationRow?>(null);
        public Task<bool> MarkViewedAsync(Guid id, Guid userId, CancellationToken ct) => Task.FromResult(false);
        public Task<bool> MarkAckedAsync(Guid id, Guid userId, CancellationToken ct) => Task.FromResult(false);
        public Task<int> MarkAllAckedAsync(Guid userId, CancellationToken ct) => Task.FromResult(0);

        public Task MarkEmailSentAsync(Guid id, DateTime? sentUtc, string? error, CancellationToken ct)
        {
            EmailStamps.Add((id, sentUtc, error));
            return Task.CompletedTask;
        }
    }

    private sealed class StubNotifier : IUserNotifier
    {
        public List<(Guid UserId, UserNotificationPush Payload)> Pushed { get; } = new();

        public Task NotifyMentionAsync(Guid userId, UserNotificationPush payload, CancellationToken ct)
        {
            Pushed.Add((userId, payload));
            return Task.CompletedTask;
        }
    }

    private sealed class StubUsers : IUserService
    {
        private readonly string _recipientEmail;
        public StubUsers(string recipientEmail) { _recipientEmail = recipientEmail; }

        public Task<ApplicationUser?> FindByIdAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult<ApplicationUser?>(new ApplicationUser(
                id, _recipientEmail, "", "Agent", DateTime.UtcNow, null, 0, null,
                AuthModes.Local, null, null, true));

        public Task<int> CountAsync(CancellationToken ct = default) => Task.FromResult(0);
        public Task<ApplicationUser?> CreateFirstAdminAsync(string email, string passwordHash, CancellationToken ct = default) => Task.FromResult<ApplicationUser?>(null);
        public Task<ApplicationUser?> FindByEmailAsync(string email, CancellationToken ct = default) => Task.FromResult<ApplicationUser?>(null);
        public Task<ApplicationUser?> FindByExternalAsync(string provider, string subject, CancellationToken ct = default) => Task.FromResult<ApplicationUser?>(null);
        public Task MarkInactiveAsync(Guid userId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<AgentUser>> ListAgentsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AgentUser>>(Array.Empty<AgentUser>());
        public Task<IReadOnlyList<AgentUser>> SearchAgentsAsync(string? search, int limit, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AgentUser>>(Array.Empty<AgentUser>());
        public Task<IReadOnlyList<Guid>> FilterAgentIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Guid>>(Array.Empty<Guid>());
        public Task UpdatePasswordHashAsync(Guid userId, string newHash, CancellationToken ct = default) => Task.CompletedTask;
        public Task RecordSuccessfulLoginAsync(Guid userId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> RecordFailedLoginAsync(Guid userId, int maxAttempts, int windowSeconds, int lockoutDurationSeconds, CancellationToken ct = default) => Task.FromResult(false);
    }

    private sealed class StubTaxonomy : ITaxonomyRepository
    {
        private readonly Queue? _q;

        public StubTaxonomy(string? mailbox)
        {
            _q = new Queue(
                QueueId, "Support", "support", "", "#fff", "", 0, true, false,
                DateTime.UtcNow, DateTime.UtcNow, mailbox, mailbox);
        }

        public Task<Queue?> GetQueueAsync(Guid id, CancellationToken ct) => Task.FromResult<Queue?>(_q);
        public Task<IReadOnlyList<Queue>> ListQueuesAsync(CancellationToken ct) => throw new NotImplementedException();
        public Task<Queue> CreateQueueAsync(Queue q, CancellationToken ct) => throw new NotImplementedException();
        public Task<Queue?> UpdateQueueAsync(Guid id, string name, string slug, string description, string color, string icon, int sortOrder, bool isActive, string? inbound, string? outbound, string? inboundFolderId, string? inboundFolderName, CancellationToken ct) => throw new NotImplementedException();
        public Task<DeleteResult> DeleteQueueAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<Status>> ListStatusesAsync(CancellationToken ct) => throw new NotImplementedException();
        public Task<Status?> GetStatusAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<Status> CreateStatusAsync(Status s, CancellationToken ct) => throw new NotImplementedException();
        public Task<Status?> UpdateStatusAsync(Guid id, string name, string slug, string stateCategory, string color, string icon, int sortOrder, bool isActive, bool isDefault, CancellationToken ct) => throw new NotImplementedException();
        public Task<DeleteResult> DeleteStatusAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<Priority>> ListPrioritiesAsync(CancellationToken ct) => throw new NotImplementedException();
        public Task<Priority?> GetPriorityAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<Priority> CreatePriorityAsync(Priority p, CancellationToken ct) => throw new NotImplementedException();
        public Task<Priority?> UpdatePriorityAsync(Guid id, string name, string slug, int level, string color, string icon, int sortOrder, bool isActive, bool isDefault, CancellationToken ct) => throw new NotImplementedException();
        public Task<DeleteResult> DeletePriorityAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<Category>> ListCategoriesAsync(CancellationToken ct) => throw new NotImplementedException();
        public Task<Category?> GetCategoryAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<Category> CreateCategoryAsync(Category c, CancellationToken ct) => throw new NotImplementedException();
        public Task<Category?> UpdateCategoryAsync(Guid id, Guid? parentId, string name, string slug, string description, int sortOrder, bool isActive, CancellationToken ct) => throw new NotImplementedException();
        public Task<DeleteResult> DeleteCategoryAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
    }

    private sealed class StubGraph : IGraphMailClient
    {
        public List<GraphOutboundMessage> Sent { get; } = new();
        public bool ThrowOnSend { get; set; }

        public Task<GraphSentMailResult> SendMailAsync(GraphOutboundMessage message, CancellationToken ct)
        {
            if (ThrowOnSend) throw new InvalidOperationException("Graph boom");
            Sent.Add(message);
            return Task.FromResult(new GraphSentMailResult("id@graph", DateTimeOffset.UtcNow));
        }

        public Task<GraphFullMessage> FetchMessageAsync(string mbx, string id, CancellationToken ct) => throw new NotImplementedException();
        public Task<Stream> FetchRawMessageAsync(string mbx, string id, CancellationToken ct) => throw new NotImplementedException();
        public Task<GraphDeltaPage> ListInboxDeltaAsync(string m, string f, string? d, int b, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<GraphMailFolderInfo>> ListMailFoldersAsync(string m, CancellationToken ct) => throw new NotImplementedException();
        public Task<TimeSpan> PingAsync(string mbx, CancellationToken ct) => Task.FromResult(TimeSpan.Zero);
        public Task MarkAsReadAsync(string m, string id, CancellationToken ct) => Task.CompletedTask;
        public Task MoveAsync(string m, string id, string f, CancellationToken ct) => Task.CompletedTask;
        public Task<string> EnsureFolderAsync(string m, string n, CancellationToken ct) => Task.FromResult("f");
        public Task<Stream> FetchAttachmentBytesAsync(string m, string id, string aid, CancellationToken ct) => throw new NotImplementedException();
    }

    private sealed class StubSettings : ISettingsService
    {
        private readonly Dictionary<string, string> _values = new();

        public void Set(string key, string value) => _values[key] = value;

        public Task EnsureDefaultsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<T> GetAsync<T>(string key, CancellationToken cancellationToken = default)
        {
            if (!_values.TryGetValue(key, out var raw))
            {
                // Defaults that matter for the happy path.
                if (key == SettingKeys.Notifications.MentionEmailEnabled) return Task.FromResult((T)(object)true);
                if (key == SettingKeys.App.PublicBaseUrl) return Task.FromResult((T)(object)string.Empty);
                return Task.FromResult(default(T)!);
            }
            if (typeof(T) == typeof(bool)) return Task.FromResult((T)(object)(raw == "true"));
            if (typeof(T) == typeof(int)) return Task.FromResult((T)(object)int.Parse(raw));
            if (typeof(T) == typeof(long)) return Task.FromResult((T)(object)long.Parse(raw));
            if (typeof(T) == typeof(string)) return Task.FromResult((T)(object)raw);
            return Task.FromResult(default(T)!);
        }

        public Task SetAsync<T>(string key, T value, string actor, string actorRole, CancellationToken cancellationToken = default)
        {
            _values[key] = value?.ToString() ?? "";
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<SettingEntry>> ListAsync(string? category = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SettingEntry>>(Array.Empty<SettingEntry>());
    }
}
