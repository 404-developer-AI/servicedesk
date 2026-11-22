using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Forms;
using Servicedesk.Infrastructure.Health;
using Servicedesk.Infrastructure.Mail.Attachments;
using Servicedesk.Infrastructure.Settings;
using Servicedesk.Infrastructure.Storage;

namespace Servicedesk.Infrastructure.Calendar;

/// What a form's mapping + answers say the appointment(s) should be.
public sealed record CalendarAppointment(
    string Title,
    DateTime StartUtc,
    DateTime EndUtc,
    IReadOnlyList<(Guid Id, string Name, bool Organizer)> Technicians,
    string Address,
    bool IsTeams,
    IReadOnlyList<CalendarAttendee> Invitees,
    string Message,
    IReadOnlyList<Guid> AttachmentIds,
    bool ShareAttachments);

/// v0.1.61 — keeps a form's Outlook appointments in step with the form.
/// One-way (servicedesk → Outlook). A save only records the plan; the
/// worker writes to Graph, so a Graph outage never blocks a save and every
/// failure is retried with a growing pause (then waits for "Retry").
///
/// Rules (owner decisions):
/// - Plain appointment: one per technician in their own calendar (no
///   organizer, no invitation); the body names the colleague(s).
/// - Teams meeting: one event in the organizer's calendar (marked, else the
///   first technician); the other technicians and the invitees attend.
///   Changing the organizer afterwards cancels and recreates the meeting
///   (new Teams link) — the form warns before saving.
/// - The body is the full internal form note only when nobody outside the
///   technicians is invited; with invitees it is the "message for attendees"
///   and files go along only when "share attachments" is ticked.
public interface ICalendarSyncService
{
    /// Server-side gate for saving a form whose template plans in Outlook.
    /// Null = allowed; otherwise the reason (403 for the caller).
    Task<string?> CheckPlannerAsync(Guid userId, string? amr, CancellationToken ct);
    /// After a save: record the plan from the live mapping and wake the worker.
    Task PlanAsync(Guid formId, CancellationToken ct);
    /// After an admin removed the form: cancel its appointments.
    Task CancelAllAsync(Guid formId, CancellationToken ct);
    Task<IReadOnlyList<CalendarEventRow>> StatusAsync(Guid formId, CancellationToken ct);
    Task<int> RetryAsync(Guid formId, CancellationToken ct);
    /// One worker pass: process due rows. Returns rows processed.
    Task<int> ProcessDueAsync(CancellationToken ct);
}

/// Wakes <see cref="CalendarSyncWorker"/> right after a save instead of
/// waiting for its next tick.
public sealed class CalendarSyncSignal
{
    private readonly SemaphoreSlim _signal = new(0, 1);
    public void Wake() { if (_signal.CurrentCount == 0) { try { _signal.Release(); } catch (SemaphoreFullException) { } } }
    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct) => _signal.WaitAsync(timeout, ct);
}

public sealed class CalendarSyncService : ICalendarSyncService
{
    public const string AmrMicrosoft = "ext";

    private readonly ICalendarSyncRepository _repo;
    private readonly IGraphCalendarClient _graph;
    private readonly ISettingsService _settings;
    private readonly Auth.IUserService _users;
    private readonly IAttachmentRepository _attachments;
    private readonly IBlobStore _blobs;
    private readonly IAuditLogger _audit;
    private readonly CalendarSyncSignal _signal;
    private readonly Realtime.ITicketListNotifier _tickets;
    private readonly ILogger<CalendarSyncService> _logger;

    public CalendarSyncService(
        ICalendarSyncRepository repo, IGraphCalendarClient graph, ISettingsService settings, Auth.IUserService users,
        IAttachmentRepository attachments, IBlobStore blobs, IAuditLogger audit, CalendarSyncSignal signal,
        Realtime.ITicketListNotifier tickets, ILogger<CalendarSyncService> logger)
    {
        _repo = repo;
        _graph = graph;
        _settings = settings;
        _users = users;
        _attachments = attachments;
        _blobs = blobs;
        _audit = audit;
        _signal = signal;
        _tickets = tickets;
        _logger = logger;
    }

    public async Task<string?> CheckPlannerAsync(Guid userId, string? amr, CancellationToken ct)
    {
        if (!await EnabledAsync(ct)) return null; // feature off: the form saves, nothing is planned
        if (!await _users.GetCalendarPlanningEnabledAsync(userId, ct))
            return "This form plans appointments in Outlook, and you don't have 'Plan in calendars'. Ask an admin to turn it on for you.";
        if (!string.Equals(amr, AmrMicrosoft, StringComparison.Ordinal))
            return "Planning in Outlook needs a Microsoft 365 sign-in. Sign out and sign in with Microsoft to save this form.";
        return null;
    }

    public async Task PlanAsync(Guid formId, CancellationToken ct)
    {
        if (!await EnabledAsync(ct)) return;
        var ctx = await _repo.GetFormContextAsync(formId, ct);
        if (ctx is null) return;
        var mapping = FormOutlookMapping.Parse(ctx.OutlookJson);
        if (ctx.Deleted || mapping is null || !mapping.Enabled)
        {
            await _repo.ApplyPlanAsync(formId, ctx.TicketId, Array.Empty<CalendarPlanEntry>(), ct);
        }
        else
        {
            var appt = Read(mapping, FormDefinition.Parse(ctx.SnapshotJson), ParseAnswers(ctx.AnswersJson), ctx);
            await _repo.ApplyPlanAsync(formId, ctx.TicketId, Plan(appt), ct);
        }
        _signal.Wake();
    }

    public async Task CancelAllAsync(Guid formId, CancellationToken ct)
    {
        var ctx = await _repo.GetFormContextAsync(formId, ct);
        if (ctx is null) return;
        await _repo.ApplyPlanAsync(formId, ctx.TicketId, Array.Empty<CalendarPlanEntry>(), ct);
        _signal.Wake();
    }

    public Task<IReadOnlyList<CalendarEventRow>> StatusAsync(Guid formId, CancellationToken ct) => _repo.ListForFormAsync(formId, ct);

    public async Task<int> RetryAsync(Guid formId, CancellationToken ct)
    {
        var n = await _repo.RetryFailedAsync(formId, ct);
        if (n > 0) _signal.Wake();
        return n;
    }

    /// The desired rows: a Teams meeting = one row for the organizer; a plain
    /// appointment = one row per technician. No times or no technicians = none.
    public static IReadOnlyList<CalendarPlanEntry> Plan(CalendarAppointment? appt)
    {
        if (appt is null || appt.Technicians.Count == 0) return Array.Empty<CalendarPlanEntry>();
        if (appt.IsTeams)
        {
            var organizer = appt.Technicians.FirstOrDefault(t => t.Organizer);
            if (organizer.Id == Guid.Empty) organizer = appt.Technicians[0];
            return new[] { new CalendarPlanEntry(organizer.Id, true) };
        }
        return appt.Technicians.Select(t => new CalendarPlanEntry(t.Id, false)).ToList();
    }

    /// Reads the mapped answers. Null when the appointment is not complete
    /// (no start/end, end not after start).
    public static CalendarAppointment? Read(FormOutlookMapping m, FormDefinition def, JsonObject answers, CalendarFormContext ctx)
    {
        JsonNode? A(Guid? id) => id is { } g && def.Fields.Any(f => f.Id == g) ? answers[g.ToString()] : null;
        string Text(Guid? id)
        {
            var f = id is { } g ? def.Fields.FirstOrDefault(x => x.Id == g) : null;
            return f is null ? string.Empty : FormRenderer.DisplayValue(f, answers[f.Id.ToString()], _ => string.Empty).Trim();
        }
        bool Bool(Guid? id) => A(id) is JsonValue v && v.GetValueKind() == JsonValueKind.True;

        var (start, end) = m.ReadTimes(def, answers);
        if (start is null || end is null || end <= start) return null;

        var techs = new List<(Guid, string, bool)>();
        switch (A(m.TechniciansFieldId))
        {
            case JsonArray arr:
                foreach (var o in arr.OfType<JsonObject>())
                    if (Guid.TryParse(Str(o["id"]), out var tid))
                        techs.Add((tid, Str(o["name"]), o["organizer"] is JsonValue ov && ov.GetValueKind() == JsonValueKind.True));
                break;
            case JsonObject single when Guid.TryParse(Str(single["id"]), out var sid):
                techs.Add((sid, Str(single["name"]), false));
                break;
        }

        var invitees = new List<CalendarAttendee>();
        if (A(m.InviteesFieldId) is JsonArray people)
            foreach (var p in people.OfType<JsonObject>())
                if (Str(p["email"]) is { Length: > 0 } email)
                    invitees.Add(new CalendarAttendee(email, Str(p["name"])));

        var files = new List<Guid>();
        if (A(m.AttachmentsFieldId) is JsonArray fa)
            foreach (var f in fa.OfType<JsonObject>())
                if (Guid.TryParse(Str(f["id"]), out var fid)) files.Add(fid);

        var title = Text(m.TitleFieldId);
        if (title.Length == 0) title = $"{ctx.FormName} · #{ctx.TicketNumber}";
        var isTeams = Bool(m.TeamsFieldId);
        return new CalendarAppointment(
            title.Length > 250 ? title[..250] : title,
            start.Value, end.Value, techs,
            isTeams ? string.Empty : Text(m.AddressFieldId).Replace("\n", ", "),
            isTeams,
            isTeams ? invitees : Array.Empty<CalendarAttendee>(),
            Text(m.MessageFieldId),
            files,
            Bool(m.ShareAttachmentsFieldId));
    }

    public async Task<int> ProcessDueAsync(CancellationToken ct)
    {
        if (!await EnabledAsync(ct)) return 0;
        var maxAttempts = Math.Clamp(await IntAsync(SettingKeys.Calendar.MaxAttempts, 8, ct), 1, 20);
        var due = await _repo.ListDueAsync(maxAttempts, 20, ct);
        foreach (var row in due)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await ProcessRowAsync(row, ct);
            }
            catch (CalendarSyncException ex)
            {
                if (ex.Gone && row.GraphEventId is not null && row.Status != CalendarEventStatus.CancelPending)
                {
                    // Deleted in Outlook: forget it; the next pass creates it again.
                    await _repo.ForgetEventAsync(row.Id, ct);
                    await _repo.MarkFailedAsync(row.Id, ex.Message, DateTime.UtcNow, ct);
                    continue;
                }
                await FailAsync(row, ex.Message, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Calendar sync failed for row {RowId}.", row.Id);
                await FailAsync(row, "Unexpected error while writing to Outlook.", ct);
            }
        }
        return due.Count;
    }

    private async Task FailAsync(CalendarEventRow row, string error, CancellationToken ct)
    {
        // 1, 2, 4 … minutes, capped at an hour.
        var wait = TimeSpan.FromMinutes(Math.Min(60, Math.Pow(2, Math.Min(row.Attempts, 6))));
        await _repo.MarkFailedAsync(row.Id, error, DateTime.UtcNow + wait, ct);
        try { await _tickets.NotifyUpdatedAsync(row.TicketId, ct); } catch { /* cosmetic */ }
    }

    private async Task ProcessRowAsync(CalendarEventRow row, CancellationToken ct)
    {
        if (row.Status == CalendarEventStatus.CancelPending)
        {
            if (row.GraphEventId is not null)
                await _graph.CancelEventAsync(row.UserEmail, row.GraphEventId, row.IsTeams,
                    "This appointment was cancelled in the servicedesk.", ct);
            await _repo.MarkCancelledAsync(row.Id, ct);
            await AuditAsync("calendar.event.cancelled", row, null, ct);
            await NotifyAsync(row.TicketId, ct);
            return;
        }

        var ctx = await _repo.GetFormContextAsync(row.FormId, ct);
        var mapping = ctx is null ? null : FormOutlookMapping.Parse(ctx.OutlookJson);
        var def = ctx is null ? null : FormDefinition.Parse(ctx.SnapshotJson);
        var appt = ctx is null || mapping is null || !mapping.Enabled || ctx.Deleted
            ? null
            : Read(mapping, def!, ParseAnswers(ctx.AnswersJson), ctx);
        if (appt is null)
        {
            // The form no longer describes an appointment: nothing to write.
            if (row.GraphEventId is not null)
                await _graph.CancelEventAsync(row.UserEmail, row.GraphEventId, row.IsTeams, "This appointment was cancelled in the servicedesk.", ct);
            await _repo.MarkCancelledAsync(row.Id, ct);
            await NotifyAsync(row.TicketId, ct);
            return;
        }

        // Safety net for the save-time rule: never invite people from a project ticket.
        if (appt.Invitees.Count > 0 && ctx!.TicketIsProject)
            throw new CalendarSyncException("Project tickets are internal: invitations to people outside the team are not sent.");

        if (row.Recreate && row.GraphEventId is not null)
        {
            await _graph.CancelEventAsync(row.UserEmail, row.GraphEventId, true, "This meeting was moved to a new organizer.", ct);
            await _repo.ForgetEventAsync(row.Id, ct);
            row.GraphEventId = null;
        }

        var spec = await BuildSpecAsync(row, appt, ctx!, def!, ct);
        var result = row.GraphEventId is null
            ? await _graph.CreateEventAsync(row.UserEmail, row.CalendarId, spec, ct)
            : await _graph.UpdateEventAsync(row.UserEmail, row.GraphEventId, spec, ct);

        // Files: only when the set changed since the last sync.
        var share = appt.Invitees.Count == 0 || appt.ShareAttachments;
        var fileIds = share ? appt.AttachmentIds : Array.Empty<Guid>();
        var key = string.Join(",", fileIds.OrderBy(x => x));
        if (key != row.SyncedAttachments || row.GraphEventId is null)
        {
            var files = await LoadFilesAsync(fileIds, ct);
            if (files.Count > 0 || row.SyncedAttachments.Length > 0)
                await _graph.ReplaceAttachmentsAsync(row.UserEmail, result.Id, files, ct);
        }

        var wasNew = row.GraphEventId is null;
        await _repo.MarkSyncedAsync(row.Id, result, ctx!.Version, key, ct);
        await AuditAsync(wasNew ? "calendar.event.created" : "calendar.event.updated", row,
            new { isTeams = appt.IsTeams, invitees = appt.Invitees.Count, files = fileIds.Count }, ct);

        // An invitation to people outside the team is outbound mail: on record.
        if (wasNew && appt.Invitees.Count > 0)
        {
            var who = string.Join(", ", appt.Invitees.Select(i => string.IsNullOrWhiteSpace(i.Name) ? i.Email : $"{i.Name} <{i.Email}>"));
            await _repo.AddTimelineNoteAsync(row.TicketId,
                $"Teams meeting \"{appt.Title}\" sent to {who}.",
                JsonSerializer.Serialize(new { calendarEvent = row.Id, formId = row.FormId, invitation = true }), ct);
        }
        await NotifyAsync(row.TicketId, ct);
    }

    private async Task<CalendarEventSpec> BuildSpecAsync(
        CalendarEventRow row, CalendarAppointment appt, CalendarFormContext ctx, FormDefinition def, CancellationToken ct)
    {
        var tz = await HealthTimeFormat.ResolveDisplayZoneAsync(_settings, ct);
        var baseUrl = (await StringAsync(SettingKeys.App.PublicBaseUrl, string.Empty, ct)).TrimEnd('/');
        var ticketLink = baseUrl.Length > 0 ? $"{baseUrl}/tickets/{ctx.TicketId}" : string.Empty;

        string body;
        if (appt.Invitees.Count > 0)
        {
            // People outside the team: never the internal note.
            var msg = appt.Message.Length > 0 ? appt.Message : appt.Title;
            body = "<p>" + E(msg).Replace("\n", "<br>") + "</p>";
        }
        else
        {
            var answers = ParseAnswers(ctx.AnswersJson);
            var note = FormRenderer.RenderNoteHtml(ctx.FormId, ctx.FormName, 1, def, answers, null, null,
                dt => HealthTimeFormat.Local(dt, tz));
            note = System.Text.RegularExpressions.Regex.Replace(note, "<span class=\"sd-form-pill\"[^>]*>.*?</span>", string.Empty);
            var sb = new StringBuilder();
            sb.Append("<p><strong>#").Append(ctx.TicketNumber).Append(" ").Append(E(ctx.TicketSubject)).Append("</strong>");
            if (ticketLink.Length > 0) sb.Append(" — <a href=\"").Append(E(ticketLink)).Append("\">open ticket</a>");
            sb.Append("</p>");
            var others = appt.Technicians.Where(t => t.Id != row.UserId).Select(t => t.Name).Where(n => n.Length > 0).ToList();
            if (!appt.IsTeams && others.Count > 0)
                sb.Append("<p>Together with ").Append(E(string.Join(", ", others))).Append(".</p>");
            sb.Append(note);
            body = sb.ToString();
        }

        var attendees = new List<CalendarAttendee>();
        if (appt.IsTeams)
        {
            var users = await _repo.GetUsersAsync(appt.Technicians.Select(t => t.Id).Where(id => id != row.UserId).ToList(), ct);
            attendees.AddRange(users.Values.Where(u => u.IsActive).Select(u => new CalendarAttendee(u.Email, u.Name)));
            attendees.AddRange(appt.Invitees);
        }

        return new CalendarEventSpec(
            appt.Title, body, appt.StartUtc, appt.EndUtc, appt.Address, appt.IsTeams,
            attendees.DistinctBy(a => a.Email.ToLowerInvariant()).ToList(),
            await StringAsync(SettingKeys.Calendar.ShowAs, "busy", ct),
            Math.Clamp(await IntAsync(SettingKeys.Calendar.ReminderMinutes, 15, ct), 0, 10080),
            await StringAsync(SettingKeys.Calendar.Category, string.Empty, ct),
            row.Id.ToString("N"));
    }

    private async Task<IReadOnlyList<CalendarFile>> LoadFilesAsync(IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return Array.Empty<CalendarFile>();
        var rows = await _attachments.GetByIdsAsync(ids, ct);
        var files = new List<CalendarFile>();
        foreach (var id in ids)
        {
            if (!rows.TryGetValue(id, out var a) || a.ContentHash is null || a.ProcessingState != "Ready") continue;
            await using var stream = await _blobs.OpenReadAsync(a.ContentHash, ct);
            if (stream is null) continue;
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms, ct);
            files.Add(new CalendarFile(string.IsNullOrWhiteSpace(a.OriginalFilename) ? "attachment" : a.OriginalFilename,
                a.MimeType, ms.ToArray()));
        }
        return files;
    }

    private async Task NotifyAsync(Guid ticketId, CancellationToken ct)
    {
        try { await _tickets.NotifyUpdatedAsync(ticketId, ct); } catch { /* cosmetic */ }
    }

    private Task AuditAsync(string eventType, CalendarEventRow row, object? extra, CancellationToken ct)
        => _audit.LogAsync(new AuditEvent(
            EventType: eventType,
            Actor: "system",
            ActorRole: "System",
            Target: row.TicketId.ToString(),
            ClientIp: null,
            UserAgent: null,
            Payload: new { formId = row.FormId, calendarRow = row.Id, mailbox = row.UserEmail, extra }), ct);

    private async Task<bool> EnabledAsync(CancellationToken ct)
    {
        return await _settings.GetOrAsync<bool>(SettingKeys.Calendar.Enabled, false, ct);
    }

    private async Task<int> IntAsync(string key, int fallback, CancellationToken ct)
    {
        return await _settings.GetOrAsync<int>(key, fallback, ct);
    }

    private async Task<string> StringAsync(string key, string fallback, CancellationToken ct)
    {
        return (await _settings.GetOrAsync<string>(key, fallback, ct)) ?? fallback;
    }

    private static JsonObject ParseAnswers(string json)
    {
        try { return JsonNode.Parse(json) as JsonObject ?? new JsonObject(); }
        catch (JsonException) { return new JsonObject(); }
    }

    private static string Str(JsonNode? n) => n is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : string.Empty;
    private static string E(string s) => System.Net.WebUtility.HtmlEncode(s);
}
