using Azure.Identity;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;
using Servicedesk.Infrastructure.Mail.Graph;
using Servicedesk.Infrastructure.Secrets;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Calendar;

/// One calendar of a user (Settings → Users dropdown).
public sealed record CalendarInfo(string Id, string Name, bool IsDefault, bool CanEdit);

/// One busy block from free/busy. Subject/location are empty for private
/// items or when the mailbox only shares free/busy.
public sealed record CalendarBusyItem(DateTime StartUtc, DateTime EndUtc, string Status, string Subject, string Location, bool IsPrivate);

public sealed record CalendarAttendee(string Email, string Name);

public sealed record CalendarFile(string Name, string ContentType, byte[] Content);

/// What one appointment should look like.
public sealed record CalendarEventSpec(
    string Subject,
    string BodyHtml,
    DateTime StartUtc,
    DateTime EndUtc,
    string Location,
    bool IsTeams,
    IReadOnlyList<CalendarAttendee> Attendees,
    string ShowAs,
    int ReminderMinutes,
    string Category,
    /// Idempotency key for create (Graph transactionId): a retried create
    /// after a lost response never makes a second appointment.
    string TransactionId);

public sealed record CalendarEventResult(string Id, string? ICalUId, string? WebLink, string? JoinUrl);

/// A Graph call that failed; <see cref="Gone"/> = the event no longer exists
/// (deleted in Outlook) — the sync treats that as "create again" / "done".
public sealed class CalendarSyncException : Exception
{
    public bool Gone { get; }
    public CalendarSyncException(string message, bool gone = false, Exception? inner = null) : base(message, inner) => Gone = gone;
}

/// v0.1.61 — Microsoft Graph calendar access for the planning flow. App-only
/// (client credentials, the same app registration as mail), so it needs the
/// Calendars.ReadWrite application permission — scoped by the admin to the
/// technicians' mailboxes with an Exchange RBAC for Applications policy.
/// Every call carries Prefer: IdType="ImmutableId" (event ids survive moves
/// between folders), all-or-nothing like the mail client.
public interface IGraphCalendarClient
{
    Task<IReadOnlyList<CalendarInfo>> ListCalendarsAsync(string mailbox, CancellationToken ct);
    /// Free/busy for several mailboxes over one window (max 100 per call).
    Task<IReadOnlyDictionary<string, IReadOnlyList<CalendarBusyItem>>> GetScheduleAsync(
        IReadOnlyList<string> mailboxes, DateTime startUtc, DateTime endUtc, CancellationToken ct);
    Task<CalendarEventResult> CreateEventAsync(string mailbox, string? calendarId, CalendarEventSpec spec, CancellationToken ct);
    Task<CalendarEventResult> UpdateEventAsync(string mailbox, string eventId, CalendarEventSpec spec, CancellationToken ct);
    /// Meetings with attendees are cancelled (attendees get a cancellation);
    /// plain appointments are deleted. A missing event is not an error.
    Task CancelEventAsync(string mailbox, string eventId, bool isMeeting, string comment, CancellationToken ct);
    /// Replaces every file attachment on the event with <paramref name="files"/>.
    Task ReplaceAttachmentsAsync(string mailbox, string eventId, IReadOnlyList<CalendarFile> files, CancellationToken ct);
}

public sealed class GraphCalendarClient : IGraphCalendarClient
{
    private static readonly string[] Scopes = ["https://graph.microsoft.com/.default"];
    /// Graph's limit for a file attachment posted in one request.
    private const int SmallAttachmentMax = 3 * 1024 * 1024;

    private readonly ISettingsService _settings;
    private readonly IProtectedSecretStore _secrets;
    private readonly GraphServiceClientCache _clients = new(CreateClient);

    public GraphCalendarClient(ISettingsService settings, IProtectedSecretStore secrets)
    {
        _settings = settings;
        _secrets = secrets;
    }

    private static GraphServiceClient CreateClient(string tenantId, string clientId, string clientSecret)
    {
        var handlers = GraphClientFactory.CreateDefaultHandlers();
        handlers.Add(new ImmutableIdHandler());
        var http = GraphClientFactory.Create(handlers);
        return new GraphServiceClient(http, new ClientSecretCredential(tenantId, clientId, clientSecret), Scopes);
    }

    private async Task<GraphServiceClient> ClientAsync(CancellationToken ct)
    {
        var tenantId = await _settings.GetAsync<string>(SettingKeys.Graph.TenantId, ct);
        var clientId = await _settings.GetAsync<string>(SettingKeys.Graph.ClientId, ct);
        var secret = await _secrets.GetAsync(ProtectedSecretKeys.GraphClientSecret, ct);
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(secret))
            throw new CalendarSyncException("Microsoft 365 is not configured (tenant, client id or secret missing).");
        return _clients.Get(tenantId, clientId, secret);
    }

    public async Task<IReadOnlyList<CalendarInfo>> ListCalendarsAsync(string mailbox, CancellationToken ct)
    {
        var graph = await ClientAsync(ct);
        var page = await Call(() => graph.Users[mailbox].Calendars.GetAsync(c =>
        {
            c.QueryParameters.Select = ["id", "name", "isDefaultCalendar", "canEdit"];
            c.QueryParameters.Top = 50;
        }, ct));
        return (page?.Value ?? new List<Microsoft.Graph.Models.Calendar>())
            .Where(c => !string.IsNullOrEmpty(c.Id))
            .Select(c => new CalendarInfo(c.Id!, c.Name ?? "Calendar", c.IsDefaultCalendar == true, c.CanEdit != false))
            .ToList();
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<CalendarBusyItem>>> GetScheduleAsync(
        IReadOnlyList<string> mailboxes, DateTime startUtc, DateTime endUtc, CancellationToken ct)
    {
        var result = new Dictionary<string, IReadOnlyList<CalendarBusyItem>>(StringComparer.OrdinalIgnoreCase);
        if (mailboxes.Count == 0) return result;
        var graph = await ClientAsync(ct);
        var body = new Microsoft.Graph.Users.Item.Calendar.GetSchedule.GetSchedulePostRequestBody
        {
            Schedules = mailboxes.Take(100).ToList(),
            StartTime = Dtz(startUtc),
            EndTime = Dtz(endUtc),
            AvailabilityViewInterval = 15,
        };
        var resp = await Call(() => graph.Users[mailboxes[0]].Calendar.GetSchedule
            .PostAsGetSchedulePostResponseAsync(body, cancellationToken: ct));
        foreach (var info in resp?.Value ?? new List<ScheduleInformation>())
        {
            if (string.IsNullOrEmpty(info.ScheduleId)) continue;
            var items = (info.ScheduleItems ?? new List<ScheduleItem>())
                .Where(i => i.Status is not FreeBusyStatus.Free)
                .Select(i => new CalendarBusyItem(
                    ParseUtc(i.Start), ParseUtc(i.End),
                    i.Status?.ToString() ?? "Busy",
                    i.IsPrivate == true ? string.Empty : i.Subject ?? string.Empty,
                    i.IsPrivate == true ? string.Empty : i.Location ?? string.Empty,
                    i.IsPrivate == true))
                .ToList();
            result[info.ScheduleId] = items;
        }
        return result;
    }

    public async Task<CalendarEventResult> CreateEventAsync(string mailbox, string? calendarId, CalendarEventSpec spec, CancellationToken ct)
    {
        var graph = await ClientAsync(ct);
        var evt = ToEvent(spec, forCreate: true);
        var created = await Call(() => string.IsNullOrEmpty(calendarId)
            ? graph.Users[mailbox].Events.PostAsync(evt, cancellationToken: ct)
            : graph.Users[mailbox].Calendars[calendarId].Events.PostAsync(evt, cancellationToken: ct));
        if (created?.Id is null) throw new CalendarSyncException("Outlook did not return the new appointment.");
        return new CalendarEventResult(created.Id, created.ICalUId, created.WebLink, created.OnlineMeeting?.JoinUrl);
    }

    public async Task<CalendarEventResult> UpdateEventAsync(string mailbox, string eventId, CalendarEventSpec spec, CancellationToken ct)
    {
        var graph = await ClientAsync(ct);
        var updated = await Call(() => graph.Users[mailbox].Events[eventId].PatchAsync(ToEvent(spec, forCreate: false), cancellationToken: ct));
        return new CalendarEventResult(updated?.Id ?? eventId, updated?.ICalUId, updated?.WebLink, updated?.OnlineMeeting?.JoinUrl);
    }

    public async Task CancelEventAsync(string mailbox, string eventId, bool isMeeting, string comment, CancellationToken ct)
    {
        var graph = await ClientAsync(ct);
        try
        {
            if (isMeeting)
            {
                await Call(() => graph.Users[mailbox].Events[eventId].Cancel.PostAsync(
                    new Microsoft.Graph.Users.Item.Events.Item.Cancel.CancelPostRequestBody { Comment = comment },
                    cancellationToken: ct));
            }
            else
            {
                await Call(() => graph.Users[mailbox].Events[eventId].DeleteAsync(cancellationToken: ct));
            }
        }
        catch (CalendarSyncException ex) when (ex.Gone)
        {
            // Already gone in Outlook — the goal is reached.
        }
    }

    public async Task ReplaceAttachmentsAsync(string mailbox, string eventId, IReadOnlyList<CalendarFile> files, CancellationToken ct)
    {
        var graph = await ClientAsync(ct);
        var existing = await Call(() => graph.Users[mailbox].Events[eventId].Attachments.GetAsync(c =>
            c.QueryParameters.Select = ["id"], ct));
        foreach (var a in existing?.Value ?? new List<Attachment>())
        {
            if (a.Id is null) continue;
            await Call(() => graph.Users[mailbox].Events[eventId].Attachments[a.Id].DeleteAsync(cancellationToken: ct));
        }

        foreach (var f in files)
        {
            if (f.Content.Length <= SmallAttachmentMax)
            {
                await Call(() => graph.Users[mailbox].Events[eventId].Attachments.PostAsync(new FileAttachment
                {
                    OdataType = "#microsoft.graph.fileAttachment",
                    Name = f.Name,
                    ContentType = f.ContentType,
                    ContentBytes = f.Content,
                }, cancellationToken: ct));
                continue;
            }

            // Larger files go through an upload session (Graph allows up to
            // 150 MB per attachment this way; the tenant's message size limit
            // still applies — the form's total-size setting keeps us inside it).
            var session = await Call(() => graph.Users[mailbox].Events[eventId].Attachments.CreateUploadSession.PostAsync(
                new Microsoft.Graph.Users.Item.Events.Item.Attachments.CreateUploadSession.CreateUploadSessionPostRequestBody
                {
                    AttachmentItem = new AttachmentItem
                    {
                        AttachmentType = AttachmentType.File,
                        Name = f.Name,
                        Size = f.Content.Length,
                        ContentType = f.ContentType,
                    },
                }, cancellationToken: ct));
            if (session is null) throw new CalendarSyncException($"Outlook refused the upload of '{f.Name}'.");
            using var stream = new MemoryStream(f.Content);
            var task = new LargeFileUploadTask<FileAttachment>(session, stream, 320 * 1024 * 10, graph.RequestAdapter);
            var upload = await task.UploadAsync(cancellationToken: ct);
            if (!upload.UploadSucceeded) throw new CalendarSyncException($"Uploading '{f.Name}' to Outlook failed.");
        }
    }

    private static Event ToEvent(CalendarEventSpec spec, bool forCreate)
    {
        var evt = new Event
        {
            Subject = spec.Subject,
            Body = new ItemBody { ContentType = BodyType.Html, Content = spec.BodyHtml },
            Start = Dtz(spec.StartUtc),
            End = Dtz(spec.EndUtc),
            Location = new Location { DisplayName = spec.IsTeams ? string.Empty : spec.Location },
            Attendees = spec.Attendees.Select(a => new Attendee
            {
                EmailAddress = new EmailAddress { Address = a.Email, Name = string.IsNullOrWhiteSpace(a.Name) ? a.Email : a.Name },
                Type = AttendeeType.Required,
            }).ToList(),
            ShowAs = spec.ShowAs.ToLowerInvariant() switch
            {
                "free" => FreeBusyStatus.Free,
                "tentative" => FreeBusyStatus.Tentative,
                "oof" => FreeBusyStatus.Oof,
                "workingelsewhere" => FreeBusyStatus.WorkingElsewhere,
                _ => FreeBusyStatus.Busy,
            },
            IsReminderOn = spec.ReminderMinutes > 0,
            ReminderMinutesBeforeStart = Math.Max(0, spec.ReminderMinutes),
            Categories = string.IsNullOrWhiteSpace(spec.Category) ? new List<string>() : new List<string> { spec.Category },
        };
        if (spec.IsTeams)
        {
            evt.IsOnlineMeeting = true;
            evt.OnlineMeetingProvider = OnlineMeetingProviderType.TeamsForBusiness;
        }
        if (forCreate) evt.TransactionId = spec.TransactionId;
        return evt;
    }

    private static DateTimeTimeZone Dtz(DateTime utc) => new()
    {
        DateTime = DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
        TimeZone = "UTC",
    };

    private static DateTime ParseUtc(DateTimeTimeZone? d)
    {
        if (d?.DateTime is null) return DateTime.MinValue;
        var parsed = DateTime.Parse(d.DateTime, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);
        if (!string.IsNullOrEmpty(d.TimeZone) && !string.Equals(d.TimeZone, "UTC", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var tz = TimeZoneInfo.FindSystemTimeZoneById(d.TimeZone);
                return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified), tz);
            }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        return DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
    }

    /// Maps Graph errors onto one exception with a message an admin can act
    /// on; never includes tokens or request bodies.
    private static async Task<T> Call<T>(Func<Task<T>> call)
    {
        try { return await call(); }
        catch (ODataError e)
        {
            var code = e.Error?.Code ?? string.Empty;
            var status = e.ResponseStatusCode;
            if (status == 404 || code is "ErrorItemNotFound" or "ResourceNotFound")
                throw new CalendarSyncException("The appointment no longer exists in Outlook.", gone: true, e);
            if (status is 401 or 403 || code is "ErrorAccessDenied" or "Authorization_RequestDenied")
                throw new CalendarSyncException(
                    "Microsoft 365 refused access to this calendar. Check the Calendars.ReadWrite application permission and the Exchange application access policy for this mailbox."
                    + $" (Graph {(status > 0 ? status.ToString() : "")} {code}: {Truncate(e.Error?.Message ?? e.Message, 200)})", inner: e);
            if (code == "ErrorMailboxNotEnabledForRESTAPI" || code == "MailboxNotEnabledForRESTAPI")
                throw new CalendarSyncException("This user has no Exchange Online mailbox.", inner: e);
            var msg = e.Error?.Message ?? e.Message;
            throw new CalendarSyncException($"Outlook error ({(status > 0 ? status.ToString() : code)}): {Truncate(msg, 300)}", inner: e);
        }
        catch (HttpRequestException e)
        {
            throw new CalendarSyncException("Microsoft 365 could not be reached.", inner: e);
        }
        catch (Azure.Identity.AuthenticationFailedException e)
        {
            // App-only token request refused (wrong/expired client secret,
            // secret id pasted instead of its value, wrong tenant/client id).
            throw new CalendarSyncException(
                "The Servicedesk app could not sign in to Microsoft 365. Check the client secret (its value, not its ID) and the tenant and client ID under Settings → Microsoft 365.", inner: e);
        }
    }

    private static async Task Call(Func<Task> call)
        => await Call(async () => { await call(); return true; });

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
